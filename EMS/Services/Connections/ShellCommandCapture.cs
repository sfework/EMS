using System.Text;

namespace EMS.Services.Connections
{
    /// <summary>
    /// 交互 Shell 命令捕获器（OSC 133 shell integration 版）：
    /// 远端 shell 经一次性注入的集成脚本，在关键节点发出 OSC 133 语义标记——
    ///   133;C        命令开始输出（bash PS0，回显之后、输出之前）
    ///   133;D;&lt;码&gt;   命令结束并携带退出码（PROMPT_COMMAND / prompt 函数）
    ///   133;A        提示符开始
    /// OSC 序列被终端仿真器消费、不渲染，终端观感与手工输入完全一致（无包装、无哨兵行）。
    /// Linux 从 C 标记起收集（命令回显天然在 C 之前，无需过滤）；
    /// Windows PS 5.1 无 PS0，从挂载时即收集，用回显过滤剔除命令行及其折行碎片。
    /// 集成未就绪时，上层会以内联 133;D 标记包装命令，捕获器按 Windows 退化模式收集。
    /// conpty 重画常以光标移动序列（CSI A/B/H/G/K）替代换行：捕获器将其视作行边界
    /// 或覆盖起点，避免相邻输出粘连；集成未就绪时上层以内联 D 标记保证命令边界可判。
    /// 命令由上层信号量串行化，同一时刻至多一个捕获器活跃。
    /// </summary>
    public sealed class ShellCommandCapture
    {
        // 转义状态机：0 可见字符 1 刚收到 ESC 2 CSI 参数中 3 OSC 体中 4 OSC 后收到 ESC
        private int _esc;
        private readonly StringBuilder _osc = new();
        private readonly StringBuilder _csi = new();
        private readonly StringBuilder _line = new();
        private readonly List<string> _output = [];

        // 退化模式（无 133;D 标记）静默收尾依据：最后一段流活动的时间戳 + 两个证据——
        // 命令回显已被过滤（_echoSeen：shell 已接收命令）且回显之后再次出现裸提示符行
        // （_barePromptSeen：shell 已回到提示符）。两者齐备才允许静默判结束，
        // 防止把"命令正在安静运行（如 Start-Sleep 30 无输出）"误判为已结束
        private long _lastActivityMs;
        private bool _echoSeen;
        private bool _barePromptSeen;

        private bool _crPending;
        private bool _started;          // C 标记分段模式：命中 133;C 后开始收集；退化模式挂载即收集
        private string? _promptPrefix;  // 退化模式：从命令回显学到的提示符前缀
        private readonly bool _windows; // 退化模式（Windows 或集成未就绪）：无 C 标记，全程收集 + 回显过滤

        public int? ExitCode { get; private set; }
        public bool Completed { get; private set; }
        public IReadOnlyList<string> Output => _output;

        /// <summary>逐行回调（长时命令用：输出到达即流入会话）</summary>
        public Action<string>? OnLine { get; set; }

        /// <summary>
        /// 退化模式判完成（无 133;D 标记可依据时的兜底，上层轮询调用）：
        /// 命令回显已被过滤（shell 已接收命令）+ 静默超过阈值 + 回显后再次出现提示符
        /// （已提交的裸提示符行，或仍挂起未换行的提示符）。三者齐备才认结束，
        /// 安静运行中的长命令（无提示符复现）不会误判
        /// </summary>
        public bool IsIdleCompleted(int idleMs = 2000)
        {
            if (!_echoSeen) return false;
            if (IdleElapsedMs() < idleMs) return false;
            return _barePromptSeen || PendingIsPromptLike();
        }

        /// <summary>
        /// 提示符行判定：短行、非 ">>" 续行、以 &gt; # $ % 收束，且不是命令回显本身。
        /// 已学到提示符前缀时要求"前缀后无正文"；前缀失效（cd 换目录）时退而要求
        /// 行短且不含 '&lt;'（排除 HTML/标签类以 '&gt;' 结尾的真实输出）
        /// </summary>
        private bool IsBarePromptLine(string trimmed)
        {
            if (trimmed.Length == 0 || trimmed.Length > 80) return false;
            if (trimmed.StartsWith(">>", StringComparison.Ordinal)) return false;
            if (trimmed[^1] is not ('>' or '#' or '$' or '%')) return false;
            if (_command != null && trimmed.EndsWith(_command, StringComparison.Ordinal)) return false;
            if (_promptPrefix != null)
            {
                if (trimmed.StartsWith(_promptPrefix, StringComparison.Ordinal))
                    return trimmed.Length <= _promptPrefix.Length + 1;
            }
            return trimmed.Length <= 30 && trimmed.IndexOf('<') < 0;
        }

        private bool PendingIsPromptLike()
        {
            if (_line.Length == 0) return false;
            return IsBarePromptLine(_line.ToString().TrimEnd());
        }

        /// <summary>距最后一段流活动的毫秒数（挂载即计时）</summary>
        public int IdleElapsedMs() => (int)Math.Max(0, Environment.TickCount64 - _lastActivityMs);

        /// <summary>完成事件（命中 133;D 或 Abort）</summary>
        public event Action<ShellCommandCapture>? CompletedEvent;

        /// <param name="windows">true=Windows PowerShell（无 C 标记，启用回显过滤）</param>
        public ShellCommandCapture(bool windows)
        {
            _windows = windows;
            _started = windows;
            _lastActivityMs = Environment.TickCount64;
        }

        /// <summary>登记命令文本（回显过滤需要）</summary>
        public void SetCommand(string command) => _command = command.Trim();
        private string? _command;

        /// <summary>喂入一段已解码的共享流文本（可能含任意转义序列，可跨块）</summary>
        public void Feed(string decoded)
        {
            if (Completed) return;
            if (decoded.Length > 0) _lastActivityMs = Environment.TickCount64;
            foreach (var c in decoded)
            {
                // ESC 入口：正常态收到 ESC 进入转义状态
                if (_esc == 0 && c == '\x1b')
                {
                    _esc = 1;
                    continue;
                }

                switch (_esc)
                {
                    case 1: // ESC 之后
                        if (c == '[') { _esc = 2; continue; }
                        if (c == ']') { _osc.Clear(); _esc = 3; continue; }
                        // 字符集指定（ESC ( B / ESC ) 0 等）：还须吞掉一个最终字节，
                        // 否则 B/0/1 漏成可见字符（交互式 top 的 ACS 序列即此形态）
                        if (c is '(' or ')' or '*' or '+' or '#') { _esc = 5; continue; }
                        _esc = 0; // 单字符转义（含 ESC \ 即 ST、ESC 7/8 等）
                        continue;
                    case 5: // 字符集指定的最终字节
                        _esc = 0;
                        continue;
                    case 2: // CSI：累积参数直到最终字节 @-~
                        if (c >= '@' && c <= '~')
                        {
                            HandleCsi(_csi.ToString(), c);
                            _csi.Clear();
                            _esc = 0;
                        }
                        else _csi.Append(c);
                        continue;
                    case 3: // OSC：直到 BEL；ESC 转 ST；非法裸换行终止序列
                        if (c == '\x07') { FlushOsc(); _esc = 0; continue; }
                        if (c == '\x1b') { _esc = 4; continue; }
                        if (c is '\r' or '\n') { FlushOsc(); _esc = 0; break; } // 换行照常处理
                        _osc.Append(c);
                        continue;
                    case 4: // OSC 内 ESC（等 ST 的 '\'，任何字符都结束转义）
                        FlushOsc();
                        _esc = 0;
                        if (c is '\r' or '\n') break;
                        continue;
                }

                if (c == '\r')
                {
                    // 终端语义：回行首、后续可见字符覆盖当前行（不提交，shell 重画不产生幻影行）
                    _crPending = true;
                    continue;
                }
                if (c == '\n')
                {
                    CommitLine();
                    _crPending = false;
                    continue;
                }

                if (c == '\b')
                {
                    if (_crPending)
                    {
                        _line.Clear();
                        _crPending = false;
                    }
                    if (_line.Length > 0) _line.Length--;
                    continue;
                }
                if (c >= ' ')
                {
                    if (_crPending)
                    {
                        _line.Clear(); // \r 后首个可见字符：从行首覆盖
                        _crPending = false;
                    }
                    _line.Append(c);
                }
            }
        }

        /// <summary>
        /// CSI 最终字节处理：conpty 重画常用光标移动序列替代换行，须据此断行，
        /// 否则相邻输出粘连（如 whoami 结果与 pwd 表头拼成一行）。
        ///   A/B/d/H/f  跨行移动 → 提交当前挂起行（行边界）
        ///   G / 省参    列绝对（目标列 1）→ 等同 CR 覆盖
        ///   2K          清整行 → 清空挂起行并从行首覆盖
        ///   J           清屏区（整屏重画前）→ 提交当前行
        /// 其余（SGR m、光标保存/恢复等）不影响可见文本，忽略
        /// </summary>
        private void HandleCsi(string ps, char fin)
        {
            switch (fin)
            {
                case 'A': // 光标上移
                case 'B': // 光标下移
                case 'd': // 行绝对定位
                case 'H': // 光标定位（row;col）
                case 'f': // 光标定位（同 H）
                    CommitLine();
                    _crPending = false;
                    return;
                case 'G': // 列绝对：目标列 1（或省参）等同回到行首覆盖
                    if (ps.Length == 0 || ps == "1") _crPending = true;
                    return;
                case 'K': // 行内清除：2=整行 → 清空并从行首覆盖
                    if (ps == "2" || ps == "?2")
                    {
                        _line.Clear();
                        _crPending = true;
                    }
                    return;
                case 'J': // 屏幕区清除：整屏重画边界，先落账当前行
                    CommitLine();
                    _crPending = false;
                    return;
            }
        }

        /// <summary>处理一条完整 OSC 序列体：识别 133 语义标记，其余（窗口标题等）丢弃</summary>
        private void FlushOsc()
        {
            var body = _osc.ToString();
            _osc.Clear();
            if (Completed || body.Length == 0) return;

            if (body.StartsWith("133;", StringComparison.Ordinal))
            {
                var payload = body.Substring(4);

                if (payload == "C")
                {
                    // 命令开始输出：Linux 从此收集（此前的回显行天然被跳过）。
                    // 清空挂起行：C 之前残留的是提示符片段，不能混入首行输出
                    _line.Clear();
                    _started = true;
                    return;
                }
                if (payload.StartsWith("D", StringComparison.Ordinal))
                {
                    // 仅当已见到 C（Linux）或本就处于收集态（Windows）才认账；
                    // 否则是并发窗口里他人/手工命令的 D，忽略
                    if (!_started) return;

                    // 命令输出无结尾换行时，挂起行是最后一行输出，先落账
                    if (payload.Length > 2 && payload[1] == ';' &&
                        int.TryParse(payload.Substring(2), out var n))
                    {
                        ExitCode = n;
                    }
                    else
                    {
                        ExitCode = 0;
                    }
                    CommitPendingLine();
                    Finish();
                }
                // 133;A：提示符边界。正常路径由 D 收尾无需处理；
                // 退化模式（钩子未生效、无 D 标记）下作为"shell 已回到提示符"的证据
                if (payload == "A" && _echoSeen) _barePromptSeen = true;
            }
        }

        private void CommitLine()
        {
            var text = _line.ToString();
            _line.Clear();
            if (Completed || !_started) return;

            // 判定用去空白副本（保留原始行缩进供 top 等对齐输出使用）
            if (!TryCleanLine(text.Trim(), out var key)) return;

            // Windows（无 C 标记）：命令回显及其折行/续行碎片过滤
            if (_windows && IsEchoNoise(key)) return;

            // 原行尾粘连 ^C 时仅剥尾（保留行首缩进）
            var final = text.EndsWith("^C", StringComparison.Ordinal) && text.Trim() != "^C"
                ? text.Substring(0, text.Length - 2).TrimEnd()
                : text;
            _output.Add(final);
            OnLine?.Invoke(final);
        }

        /// <summary>提交挂起行（Windows/Linux 通用，D 收尾前落账无结尾换行的尾输出）</summary>
        private void CommitPendingLine()
        {
            if (_line.Length == 0) return;
            var raw = _line.ToString();
            _line.Clear();
            if (!TryCleanLine(raw.Trim(), out var key)) return;
            if (_windows && IsEchoNoise(key)) return;
            var final = raw.EndsWith("^C", StringComparison.Ordinal) && raw.Trim() != "^C"
                ? raw.Substring(0, raw.Length - 2).TrimEnd()
                : raw;
            _output.Add(final);
            OnLine?.Invoke(final);
        }

        /// <summary>
        /// 行清洗（判定层）：tty 对 SIGINT 回显的 "^C"——独立 ^C 行整行丢弃（返回 false）；
        /// 粘连在尾行的（"...wq^C"）剥离后返回正文供噪声判定，原文缩进由调用方另行处理
        /// </summary>
        private static bool TryCleanLine(string trimmed, out string clean)
        {
            clean = string.Empty;
            if (trimmed.Length == 0) return false;
            if (trimmed == "^C") return false;
            if (trimmed.EndsWith("^C", StringComparison.Ordinal))
            {
                clean = trimmed.Substring(0, trimmed.Length - 2);
                return clean.Trim().Length > 0;
            }
            clean = trimmed;
            return true;
        }

        /// <summary>
        /// Windows 回显噪声判定：命令回显行（EndsWith 命令，借机学提示符前缀）、
        /// 提示符开头行（裸提示符/重画）、命令折行碎片（≥8 字符且为命令子串并含 shell 语法字符）、
        /// ">>" 续行提示、纯分号碎片
        /// </summary>
        private bool IsEchoNoise(string trimmed)
        {
            // 命令回显行 = 提示符 + 完整命令（EndsWith 命中）。要求前缀呈提示符形态
            // （> # $ % 收束）、为空（重画行），或前缀尾部确系命令自身片段
            // （conpty 重绘会把"半截回显+完整回显"合并成一行，如 `echo "A"; weecho "A"; whoami`）
            // ——否则 PowerShell 错误信息里 "+ <命令原文>" 的引用行同样以命令结尾，会被误杀
            if (_command != null && trimmed.EndsWith(_command, StringComparison.Ordinal))
            {
                var p = trimmed.Substring(0, trimmed.Length - _command.Length).TrimEnd();
                if (p.Length == 0 || p[^1] is '>' or '#' or '$' or '%')
                {
                    if (p.Length > 0) _promptPrefix = p;
                    _echoSeen = true; // shell 已接收并回显命令（静默收尾的先决证据）
                    return true;
                }
                // 前缀尾部（≤16 字符，且不少于 4）是命令子串 → 判为回显重绘碎片
                if (p.Length >= 4)
                {
                    var tail = p.Substring(Math.Max(0, p.Length - 16));
                    if (_command.Contains(tail, StringComparison.Ordinal))
                    {
                        _echoSeen = true;
                        return true;
                    }
                }
            }

            // 折行首段：命令超长被折断，首行 = 提示符 + 命令前缀片段（EndsWith 整命令不命中）。
            // 命中命令起始探针（≥12 字符）且探针前确系提示符形态（> # $ % 收束）才判为回显——
            // 否则 PowerShell 错误信息里引用命令原文的行（"+ Get-ChildItem C:\xyz"）会被误杀
            if (_command != null && _command.Length >= 12)
            {
                var probe = _command.Substring(0, Math.Min(16, _command.Length));
                var at = trimmed.IndexOf(probe, StringComparison.Ordinal);
                if (at > 0)
                {
                    var p = trimmed.Substring(0, at).TrimEnd();
                    if (p.Length > 0 && p[^1] is '>' or '#' or '$' or '%')
                    {
                        _promptPrefix = p;
                        _echoSeen = true;
                        return true;
                    }
                }
            }

            if (_promptPrefix != null && trimmed.StartsWith(_promptPrefix, StringComparison.Ordinal))
            {
                // 回显之后再次出现"裸提示符"（前缀后无其他内容）= shell 已回到提示符，
                // 是静默收尾的第二证据；折行续行虽以提示符开头但不算
                if (_echoSeen &&
                    trimmed.Length <= _promptPrefix.Length + 1 &&
                    trimmed[_promptPrefix.Length..].Trim().Length == 0)
                    _barePromptSeen = true;
                return true;
            }
            if (trimmed.StartsWith(">>", StringComparison.Ordinal)) return true;
            if (trimmed.Length > 0 && trimmed.All(c => c is ';' or ' ')) return true;

            if (_command != null)
            {
                var body = trimmed;
                if (_promptPrefix != null && body.StartsWith(_promptPrefix, StringComparison.Ordinal))
                {
                    body = body.Substring(_promptPrefix.Length).Trim();
                }
                // conpty 清行重绘的半截回显：提示符被擦除后整行恰为命令的严格前缀
                // （实测 `echo INTEGRATION_CH` ⊂ `echo INTEGRATION_CHECK`）。
                // 正常输出恰好等于所输命令的前缀片段几乎不可能，误杀风险可忽略
                if (body.Length >= 4 && body.Length < _command.Length &&
                    _command.StartsWith(body, StringComparison.Ordinal))
                {
                    _echoSeen = true;
                    return true;
                }
                if (body.Length >= 8 &&
                    body.IndexOfAny(['|', '{', '}', ';', '$', '\'', '"', '\\']) >= 0 &&
                    _command.Contains(body, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>超时/断线等外部终止（保留已捕获部分）</summary>
        public void Abort()
        {
            if (!Completed) Finish();
        }

        private void Finish()
        {
            Completed = true;
            CompletedEvent?.Invoke(this);
        }
    }
}
