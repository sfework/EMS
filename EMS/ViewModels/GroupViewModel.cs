using EMS.Common;
using EMS.Models;
using System.Collections.ObjectModel;

namespace EMS.ViewModels
{
    public class GroupViewModel : NotifyPropertyChanged
    {
        private readonly ServerGroup _group;
        private string _name;
        private string _defaultAccount;
        private string _defaultPassword;
        private ObservableCollection<ServerViewModel> _servers;

        public GroupViewModel(ServerGroup group)
        {
            _group = group;
            _name = group.Name;
            _defaultAccount = group.DefaultAccount;
            _defaultPassword = group.DefaultPassword;

            _servers = new ObservableCollection<ServerViewModel>();
            if (group.Servers != null)
            {
                foreach (var server in group.Servers)
                {
                    _servers.Add(new ServerViewModel(server));
                }
            }
        }

        public string ID => _group.ID;

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged(nameof(Name));
            }
        }

        public string DefaultAccount
        {
            get => _defaultAccount;
            set
            {
                _defaultAccount = value;
                OnPropertyChanged(nameof(DefaultAccount));
            }
        }

        public string DefaultPassword
        {
            get => _defaultPassword;
            set
            {
                _defaultPassword = value;
                OnPropertyChanged(nameof(DefaultPassword));
            }
        }

        public ObservableCollection<ServerViewModel> Servers
        {
            get => _servers;
            set
            {
                _servers = value;
                OnPropertyChanged(nameof(Servers));
            }
        }

        public ServerGroup GetModel()
        {
            _group.Name = _name;
            _group.DefaultAccount = _defaultAccount;
            _group.DefaultPassword = _defaultPassword;
            return _group;
        }

        public void RefreshFromModel()
        {
            _name = _group.Name;
            _defaultAccount = _group.DefaultAccount;
            _defaultPassword = _group.DefaultPassword;

            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DefaultAccount));
            OnPropertyChanged(nameof(DefaultPassword));
        }

        public void AddServer(ServerViewModel server)
        {
            _servers.Add(server);
        }

        public void RemoveServer(ServerViewModel server)
        {
            _servers.Remove(server);
        }
    }
}
