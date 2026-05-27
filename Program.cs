using YealinkAdmin.Components;
using YealinkAdmin.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();

builder.Services.AddControllers();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<SecureCredentialStorage>();
builder.Services.AddSingleton<PhoneStore>();
builder.Services.AddSingleton<YealinkApiClient>();
builder.Services.AddSingleton<YealinkScanner>();
builder.Services.AddSingleton<YealinkWebClient>();
builder.Services.AddSingleton<YealinkConfigManager>();
builder.Services.AddSingleton<YealinkStatusClient>();

builder.Services.AddHttpClient("yealink", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    AllowAutoRedirect = true,
    UseCookies = true,
    CookieContainer = new System.Net.CookieContainer()
});

builder.Services.AddHttpClient("yealink-web", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    AllowAutoRedirect = true,
    UseCookies = true,
    CookieContainer = new System.Net.CookieContainer()
});

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

var store = app.Services.GetRequiredService<PhoneStore>();
store.Load();

app.Run();
