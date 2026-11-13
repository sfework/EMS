namespace EMS.Services.Sftp
{
    /// <summary>
    /// 远程文件条目（从 UI 文件迁出的 DTO）
    /// </summary>
    public class RemoteFileEntry
    {
        public string Name { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public string FullPath { get; set; } = string.Empty;
        public DateTime LastWriteTime { get; set; }
        public long Size { get; set; }
        public string Permissions { get; set; } = string.Empty;

        public string SizeText => IsDirectory ? "<DIR>" : Common.ByteFormat.Size(Size);
    }
}
