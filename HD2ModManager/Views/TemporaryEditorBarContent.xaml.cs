using System.Windows.Input;
using System.Windows.Controls;

namespace HD2ModManager.Views
{
    // 作用：承载一层完整底栏内容，供双层交叉淡化切换使用。
    public partial class TemporaryEditorBarContent : UserControl
    {
        public TemporaryEditorBarContent()
        {
            InitializeComponent();
        }

        private void OnEditorTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            if (DataContext is not TemporaryEditorBarPresentation presentation) return;
            if (!presentation.ConfirmEditCommand.CanExecute(null)) return;

            presentation.ConfirmEditCommand.Execute(null);
            e.Handled = true;
        }
    }
}
