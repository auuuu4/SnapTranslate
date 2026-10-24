using System.Windows;

namespace SnapTranslate;

public partial class NoteWindow : Window
{
    public NoteWindow(string note)
    {
        InitializeComponent();
        NoteText.Text = note;
        Loaded += (_, _) =>
        {
            NoteText.Focus();
            NoteText.CaretIndex = NoteText.Text.Length;
        };
    }

    public string Note => NoteText.Text;

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
