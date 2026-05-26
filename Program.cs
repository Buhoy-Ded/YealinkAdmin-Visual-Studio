<<<<<<< HEAD
=======
using Microsoft.AspNetCore.StaticFiles;
>>>>>>> 3be700de0735421e3de6a3fa9ed52d98f83113f4
using YealinkAdmin.Components;
using YealinkAdmin.Services;

var builder = WebApplication.CreateBuilder(args);

<<<<<<< HEAD
builder.WebHost.UseStaticWebAssets();

builder.Services.AddControllers();
=======
// Включаем StaticWebAssets для Production
builder.WebHost.UseStaticWebAssets();

>>>>>>> 3be700de0735421e3de6a3fa9ed52d98f83113f4
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDataProtection();
builder.Services.AddSingleton < SecureCredentialStorage > ();
<<<<<<< HEAD
builder.Services.AddSingleton < PhoneStore > ();
builder.Services.AddSingleton < YealinkApiClient > ();
builder.Services.AddSingleton < YealinkScanner > ();
builder.Services.AddSingleton < YealinkWebClient > ();
builder.Services.AddSingleton < YealinkConfigManager > ();
builder.Services.AddSingleton < YealinkStatusClient > (); // ← ДОБАВЛЕНО

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
=======
builder.Services.AddSingleton < YealinkApiClient > ();
builder.Services.AddSingleton < PhoneStore > ();
builder.Services.AddSingleton < YealinkScanner > ();
>>>>>>> 3be700de0735421e3de6a3fa9ed52d98f83113f4

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
<<<<<<< HEAD
app.MapControllers();
=======

>>>>>>> 3be700de0735421e3de6a3fa9ed52d98f83113f4
app.MapRazorComponents < App > ()
    .AddInteractiveServerRenderMode();

var store = app.Services.GetRequiredService < PhoneStore > ();
store.Load();

<<<<<<< HEAD
app.Run();
=======
app.Run();

yv$5$w@K&574zN3$4h@L5q*2@^#G4Eb#
>>>>>>> 3be700de0735421e3de6a3fa9ed52d98f83113f4
