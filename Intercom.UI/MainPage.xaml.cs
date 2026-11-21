using Intercom.UI.ViewModels;

namespace Intercom.UI;

public partial class MainPage : ContentPage
{
    public MainPage(IntercomViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

