using Avalonia;
using Avalonia.Headless;
using Mele.App;
using Mele.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Mele.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Mele.App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
