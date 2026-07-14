using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace DevFlow.Sample;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var window = Window;
        if (window is null)
            return;

        var controller = WindowCompat.GetInsetsController(window, window.DecorView);
        if (controller is not null)
            controller.AppearanceLightStatusBars = false;
    }
}
