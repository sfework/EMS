using EMS.Services.Connections;
using Xunit;

namespace EMS.Tests
{
    /// <summary>
    /// 交互 Shell 命令捕获器（OSC 133）测试：C/D 标记分段、跨块转义、
    /// 退出码、无结尾换行尾行落账、Windows 回显过滤、SIGINT 噪声
    /// </summary>
    public class ShellCommandCaptureTests
    {
        private const char Esc = '\x1b';
        private const char Bel = '\x07';

        // Linux 会话：注入生效，命令前已见 D;A，执行时发 C，结束发 D;码
        private static ShellCommandCapture LinuxRun(string outputFeed, string code = "0")
        {
            var cap = new ShellCommandCapture(windows: false);
            cap.SetCommand("echo hi");
            // shell 空闲：先有提示符标记（未收集）
            cap.Feed($"{Esc}]133;D;0{Bel}{Esc}]133;A{Bel}[root@host ~]# ");
            // 命令执行：C 标记 → 输出 → 下一个提示符前 D
            cap.Feed($"{Esc}]133;C{Bel}");
            cap.Feed(outputFeed);
            cap.Feed($"{Esc}]133;D;{code}{Bel}{Esc}]133;A{Bel}[root@host ~]# ");
            return cap;
        }

        [Fact]
        public void Linux_按CD分段收集输出与退出码()
        {
            var cap = LinuxRun("hi\r\n");
            Assert.True(cap.Completed);
            Assert.Equal(0, cap.ExitCode);
            Assert.Equal("hi", Assert.Single(cap.Output));
        }

        [Fact]
        public void Linux_命令回显天然在C之前不进输出()
        {
            // 回显行在 C 标记之前到达（readline 回显先于 PS0），天然被 _started 挡住
            var cap = new ShellCommandCapture(windows: false);
            cap.SetCommand("ls");
            cap.Feed($"[root@host ~]# ls\r\n");           // 回显（未 started）
            cap.Feed($"{Esc}]133;C{Bel}");                 // C：开始收集
            cap.Feed("file.txt\r\n");
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.True(cap.Completed);
            Assert.Equal("file.txt", Assert.Single(cap.Output));
        }

        [Fact]
        public void 多行输出顺序保留()
        {
            var cap = LinuxRun("a\r\nb\r\nc\r\n");
            Assert.Equal(["a", "b", "c"], cap.Output);
        }

        [Fact]
        public void 无结尾换行的尾输出在D前落账()
        {
            // 命令输出无结尾换行（如某些程序），挂起行须在 D 标记到达时提交
            var cap = new ShellCommandCapture(windows: false);
            cap.Feed($"{Esc}]133;C{Bel}partial-tail");
            cap.Feed($"{Esc}]133;D;3{Bel}");
            Assert.True(cap.Completed);
            Assert.Equal(3, cap.ExitCode);
            Assert.Equal("partial-tail", Assert.Single(cap.Output));
        }

        [Fact]
        public void C标记跨数据块安全()
        {
            var cap = new ShellCommandCapture(windows: false);
            cap.Feed($"{Esc}]13");          // 半截 OSC
            cap.Feed($"3;C{Bel}out1\r\n");  // 补齐 C
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.True(cap.Completed);
            Assert.Equal("out1", Assert.Single(cap.Output));
        }

        [Fact]
        public void 非133的OSC被忽略()
        {
            // 窗口标题 OSC 0、其他自定义码不得干扰
            var cap = new ShellCommandCapture(windows: false);
            cap.Feed($"{Esc}]0;user@host: ~{Bel}");
            cap.Feed($"{Esc}]633;whatever{Bel}");
            cap.Feed($"{Esc}]133;C{Bel}data\r\n");
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.True(cap.Completed);
            Assert.Equal("data", Assert.Single(cap.Output));
        }

        [Fact]
        public void D无码值按零处理()
        {
            var cap = new ShellCommandCapture(windows: false);
            cap.Feed($"{Esc}]133;C{Bel}x\r\n");
            cap.Feed($"{Esc}]133;D{Bel}");
            Assert.True(cap.Completed);
            Assert.Equal(0, cap.ExitCode);
        }

        [Fact]
        public void 未见到C时D不触发结束()
        {
            // 手工命令/他人命令的 D 标记不得误收尾（Linux 未 started 时忽略 D）
            var cap = new ShellCommandCapture(windows: false);
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.False(cap.Completed);
        }

        [Fact]
        public void 颜色序列剥离后收集可见文本()
        {
            var cap = new ShellCommandCapture(windows: false);
            cap.Feed($"{Esc}]133;C{Bel}");
            cap.Feed($"\x1b[01;34mDesktop\x1b[0m  file.txt\r\n");
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.Equal("Desktop  file.txt", Assert.Single(cap.Output));
        }

        [Fact]
        public void 字符集指定序列吞掉最终字节()
        {
            // ESC ( B / ESC ) 0 等字符集序列共 3 字节：最终字节（B/0/1）不得漏成可见字符
            // （交互式 top 的 ACS 线框序列即此形态）
            var cap = new ShellCommandCapture(windows: false);
            cap.Feed($"{Esc}]133;C{Bel}");
            cap.Feed($"{Esc}(B{Esc})0data\r\n");
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.True(cap.Completed);
            Assert.Equal("data", Assert.Single(cap.Output));
        }

        [Fact]
        public void 进度条CR重画只留最后()
        {
            var cap = new ShellCommandCapture(windows: false);
            cap.Feed($"{Esc}]133;C{Bel}");
            cap.Feed("50%\r100%\r\n");
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.Equal("100%", Assert.Single(cap.Output));
        }

        [Fact]
        public void SIGINT的CaretC回显被忽略()
        {
            // 手工 Ctrl+C：tty 回显 ^C；此后 shell 发 D;130 正常收尾
            var cap = new ShellCommandCapture(windows: false);
            cap.Feed($"{Esc}]133;C{Bel}before\r\n^C\r\n");
            cap.Feed($"{Esc}]133;D;130{Bel}");
            Assert.True(cap.Completed);
            Assert.Equal(130, cap.ExitCode);
            Assert.Equal("before", Assert.Single(cap.Output));
        }

        [Fact]
        public void 粘连在尾行的CaretC被剥离且缩进保留()
        {
            // top 进程表行有前导缩进；中断时 ^C 恰好粘连在无结尾换行的尾行上：
            // 剥离 ^C、保留正文与缩进
            var cap = new ShellCommandCapture(windows: false);
            cap.Feed($"{Esc}]133;C{Bel}header\r\n");
            cap.Feed("  62773 root      20   0       0      0      0 I   0.0   0.0   0:00.00 kworker^C");
            cap.Feed($"{Esc}]133;D;130{Bel}");
            Assert.True(cap.Completed);
            Assert.Equal(130, cap.ExitCode);
            Assert.Equal(2, cap.Output.Count);
            Assert.Equal("  62773 root      20   0       0      0      0 I   0.0   0.0   0:00.00 kworker", cap.Output[1]);
        }

        [Fact]
        public void Abort保留已捕获部分()
        {
            var cap = new ShellCommandCapture(windows: false);
            cap.Feed($"{Esc}]133;C{Bel}line-1\r\n");
            cap.Abort();
            Assert.True(cap.Completed);
            Assert.Null(cap.ExitCode);
            Assert.Equal("line-1", Assert.Single(cap.Output));
        }

        [Fact]
        public void 逐行回调实时触发()
        {
            var cap = new ShellCommandCapture(windows: false);
            var seen = new List<string>();
            cap.OnLine = seen.Add;
            cap.Feed($"{Esc}]133;C{Bel}");
            cap.Feed("A\r\nB\r\n");
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.Equal(["A", "B"], seen);
        }

        // ===== Windows 模式：挂载即收集 + 回显过滤 =====

        [Fact]
        public void Windows_命令回显与提示符行被过滤()
        {
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand("echo WB1");
            cap.Feed("PS C:\\Users\\Administrator> echo WB1\r\n");  // 命令回显（学前缀）
            cap.Feed("WB1\r\n");                                     // 真实输出
            cap.Feed("PS C:\\Users\\Administrator> \r\n");           // 提示符重画
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.True(cap.Completed);
            Assert.Equal("WB1", Assert.Single(cap.Output));
        }

        [Fact]
        public void Windows_长命令折行碎片全部剔除()
        {
            var longCmd = "1..20 | ForEach-Object { Write-Output \"tick $_\"; Start-Sleep -Milliseconds 1500 }";
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand(longCmd);
            cap.Feed($"PS C:\\Users\\Administrator> 1..20 | ForEach-Object {{ Write-Output \"tick $_\";\r\n");
            cap.Feed(" Start-Sleep -Milliseconds 1500 }\r\n");
            cap.Feed(";\r\n>>\r\n");
            cap.Feed("tick 1\r\ntick 2\r\n");
            cap.Feed($"{Esc}]133;D;1{Bel}");
            Assert.True(cap.Completed);
            Assert.Equal(1, cap.ExitCode);
            Assert.Equal(["tick 1", "tick 2"], cap.Output);
        }

        [Fact]
        public void Windows_真实输出含语法字符但非命令子串不误杀()
        {
            // 输出恰含管道符等语法字符，但不是命令的子串 → 保留
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand("dir | sort");
            cap.Feed("PS> dir | sort\r\n");
            cap.Feed("a | b | c\r\n");   // 真实输出（含 | 但不是命令子串）
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.Equal("a | b | c", Assert.Single(cap.Output));
        }

        [Fact]
        public void Windows_错误信息引用命令原文的行不误杀()
        {
            // PowerShell 报错会把命令原文回显在 "+ <命令>" 行里（前缀是 + 非提示符），
            // 折行首段探针必须要求前缀呈提示符形态，否则真实错误输出被误杀
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand("Get-ChildItem C:\\nonexistent-xyz-very-long-path");
            cap.Feed("PS C:\\> Get-ChildItem C:\\nonexistent-xyz-very-long-path\r\n"); // 命令回显
            cap.Feed("Get-ChildItem : 找不到路径\r\n");                                  // 错误首行
            cap.Feed("+ Get-ChildItem C:\\nonexistent-xyz-very-long-path\r\n");          // 错误引用命令行
            cap.Feed("    + CategoryInfo : ObjectNotFound\r\n");
            cap.Feed($"{Esc}]133;D;1{Bel}");
            // 命令回显行被过滤，其余 3 行错误输出全部保留
            Assert.Equal(3, cap.Output.Count);
            Assert.Contains("+ Get-ChildItem C:\\nonexistent-xyz-very-long-path", cap.Output);
        }

        [Fact]
        public void Windows_PS退出码False映射为1()
        {
            // PS prompt 里 $? 布尔转码：False → D;1
            var cap = new ShellCommandCapture(windows: true);
            cap.Feed("out\r\n");
            cap.Feed($"{Esc}]133;D;1{Bel}");
            Assert.Equal(1, cap.ExitCode);
        }

        // ===== conpty 重绘：CSI 光标序列行边界 + 回显碎片 =====

        [Fact]
        public void CSI光标上移序列视作行边界不粘连()
        {
            // conpty 重画用 ESC[1;1H / ESC[A 移动光标而非换行：相邻输出须各自成行，
            // 否则 whoami 结果与下一行表头粘成一行（实测 administratorPath 粘连形态）
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand("whoami");
            cap.Feed("PS> whoami\r\n");
            cap.Feed("host\\admin");
            cap.Feed($"{Esc}[1;1H");   // 光标回左上角：上句须先落账
            cap.Feed("NextLine\r\n");
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.Equal(["host\\admin", "NextLine"], cap.Output);
        }

        [Fact]
        public void CSI清行序列等同回到行首覆盖()
        {
            // ESC[2K 清整行后重写：进度条形态，只留最后内容
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand("x");
            cap.Feed($"{Esc}[2K50%{Esc}[1G{Esc}[2K100%\r\n");
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.Equal("100%", Assert.Single(cap.Output));
        }

        [Fact]
        public void 回显重绘碎片行被过滤()
        {
            // conpty 把半截回显与完整回显合并成一行（实测形态）：
            // `echo "PROBE_OK"; who` + `echo "PROBE_OK"; whoami; pwd` 拼成一行，
            // 前缀尾段确系命令子串 → 判回显碎片；真实输出行不受影响
            var cmd = "echo \"A\"; whoami; pwd";
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand(cmd);
            cap.Feed($"PS> {cmd}\r\n");
            cap.Feed($"echo \"A\"; who{cmd}\r\n");   // 半截回显 + 完整回显的重绘合并行
            cap.Feed("A\r\n");
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.Equal("A", Assert.Single(cap.Output));
        }

        [Fact]
        public void 清行重绘的无提示符半截回显被过滤()
        {
            // 实测形态：conpty 清行重绘把提示符擦掉，半截回显独占一行且恰为命令严格前缀
            // （`echo INTEGRATION_CH` ⊂ `echo INTEGRATION_CHECK`）→ 判回显碎片；
            // 真实输出 `INTEGRATION_CHECK` 不是命令前缀，不受影响
            var cmd = "echo INTEGRATION_CHECK";
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand(cmd);
            cap.Feed($"PS C:\\> {cmd}\r\n");          // 完整回显（学习提示符）
            cap.Feed("echo INTEGRATION_CH\r\n");      // 清行重绘的半截回显碎片
            cap.Feed("INTEGRATION_CHECK\r\n");        // 真实输出
            cap.Feed($"{Esc}]133;D;0{Bel}");
            Assert.Equal("INTEGRATION_CHECK", Assert.Single(cap.Output));
        }

        // ===== 退化模式静默收尾（无 D 标记兜底） =====

        [Fact]
        public void 静默收尾需回显加提示符复现双证据()
        {
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand("Start-Sleep 30");
            // 仅有回显、命令安静运行中：不得判完成（防误杀 sleep/编译等无输出命令）
            cap.Feed("PS C:\\> Start-Sleep 30\r\n");
            Assert.False(cap.IsIdleCompleted(0));
            // 输出 + 裸提示符复现：双证据齐备，静默即判完成
            cap.Feed("done-stuff\r\n");
            cap.Feed("PS C:\\> \r\n");
            Assert.True(cap.IsIdleCompleted(0));
        }

        [Fact]
        public void 静默收尾认挂起未换行的提示符()
        {
            // 提示符打印后无换行（等待输入态，留在挂起行）：同样算提示符复现
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand("dir");
            cap.Feed("PS C:\\> dir\r\n");
            cap.Feed("a.txt\r\n");
            cap.Feed("PS C:\\> ");
            Assert.True(cap.IsIdleCompleted(0));
        }

        [Fact]
        public void 无回显时静默不判完成()
        {
            // 命令尚未被 shell 接收（无回显）：即便静默也不得判完成
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand("dir");
            Assert.False(cap.IsIdleCompleted(0));
        }

        [Fact]
        public void 提示符形态行不误判为完成证据()
        {
            // >> 续行提示不是提示符复现证据；输出行以 > 收束（重定向回显类）不算裸提示符
            var cap = new ShellCommandCapture(windows: true);
            cap.SetCommand("cmd");
            cap.Feed("PS> cmd\r\n");
            cap.Feed(">>\r\n");
            Assert.False(cap.IsIdleCompleted(0));
        }
    }
}
