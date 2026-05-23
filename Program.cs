using Microsoft.AspNetCore.StaticFiles;
using YealinkAdmin.Components;
using YealinkAdmin.Services;

var builder = WebApplication.CreateBuilder(args);

// Включаем StaticWebAssets для Production
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDataProtection();
builder.Services.AddSingleton < SecureCredentialStorage > ();
builder.Services.AddSingleton < YealinkApiClient > ();
builder.Services.AddSingleton < PhoneStore > ();
builder.Services.AddSingleton < YealinkScanner > ();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents < App > ()
    .AddInteractiveServerRenderMode();

var store = app.Services.GetRequiredService < PhoneStore > ();
store.Load();

app.Run();
