using System.Windows;
using InterviewCoach.App.ViewModels;

namespace InterviewCoach.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
