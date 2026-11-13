using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace EMS.Dialogs
{
    public partial class TextEditorWindow : Window
    {
        private readonly string _filePath;

        public TextEditorWindow(string filePath)
        {
            InitializeComponent();
            _filePath = filePath;

            if (File.Exists(filePath))
            {
                Editor.Text = File.ReadAllText(filePath);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                File.WriteAllText(_filePath, Editor.Text);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}