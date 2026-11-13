using EMS.Common;
using EMS.Models;

namespace EMS.ViewModels
{
    public class ServerViewModel : NotifyPropertyChanged
    {
        private readonly Server _server;
        private string _name;
        private string _ip;
        private int _port;
        private string _account;
        private bool _inheritAccount;
        private string _password;
        private bool _inheritPassword;
        private Enums.ServerStatus _status;
        private string? _errorMessage;

        public ServerViewModel(Server server)
        {
            _server = server;
            _name = server.Name;
            _ip = server.IP;
            _port = server.Port;
            _account = server.Account;
            _inheritAccount = server.InheritAccount;
            _password = server.Password;
            _inheritPassword = server.InheritPassword;
            _status = Enums.ServerStatus.未连接;
            _errorMessage = null;
        }

        public string ID => _server.ID;
        public string GroupID => _server.GroupID;

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged(nameof(Name));
            }
        }

        public string IP
        {
            get => _ip;
            set
            {
                _ip = value;
                OnPropertyChanged(nameof(IP));
            }
        }

        public int Port
        {
            get => _port;
            set
            {
                _port = value;
                OnPropertyChanged(nameof(Port));
            }
        }

        public string Account
        {
            get => _account;
            set
            {
                _account = value;
                OnPropertyChanged(nameof(Account));
            }
        }

        public bool InheritAccount
        {
            get => _inheritAccount;
            set
            {
                _inheritAccount = value;
                OnPropertyChanged(nameof(InheritAccount));
            }
        }

        public string Password
        {
            get => _password;
            set
            {
                _password = value;
                OnPropertyChanged(nameof(Password));
            }
        }

        public bool InheritPassword
        {
            get => _inheritPassword;
            set
            {
                _inheritPassword = value;
                OnPropertyChanged(nameof(InheritPassword));
            }
        }

        public Enums.ConnectionType ConnectionType => _server.ConnectionType;

        public Enums.ServerStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(IsConnected));
            }
        }

        // 添加一个辅助属性，用于强制刷新图标
        public object StatusIcon => Status;

        /// <summary>
        /// 是否已连接（右键菜单显示"断开"还是"连接"）
        /// </summary>
        public bool IsConnected => Status == Enums.ServerStatus.已连接;

        public string? ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (_errorMessage == value) return;
                
                _errorMessage = value;
                OnPropertyChanged(nameof(ErrorMessage));
            }
        }

        public Server GetModel()
        {
            _server.Name = _name;
            _server.IP = _ip;
            _server.Port = _port;
            _server.Account = _account;
            _server.InheritAccount = _inheritAccount;
            _server.Password = _password;
            _server.InheritPassword = _inheritPassword;
            return _server;
        }

        public void RefreshFromModel()
        {
            _name = _server.Name;
            _ip = _server.IP;
            _port = _server.Port;
            _account = _server.Account;
            _inheritAccount = _server.InheritAccount;
            _password = _server.Password;
            _inheritPassword = _server.InheritPassword;

            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(IP));
            OnPropertyChanged(nameof(Port));
            OnPropertyChanged(nameof(Account));
            OnPropertyChanged(nameof(InheritAccount));
            OnPropertyChanged(nameof(Password));
            OnPropertyChanged(nameof(InheritPassword));
        }
    }
}
