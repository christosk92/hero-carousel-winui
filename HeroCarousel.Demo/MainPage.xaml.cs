using HeroCarousel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HeroCarousel.Demo;

/// <summary>
/// The main content page displayed inside the application window.
/// Add your UI logic, event handlers, and data binding here.
/// </summary>
public sealed partial class MainPage : Page
{
    private readonly ResourceLoader _resources = new();

    public MainPage()
    {
        InitializeComponent();
        HeroCarousel.ItemsSource = HeroCarouselDemoData.CreateSlides(_resources);
    }
}
