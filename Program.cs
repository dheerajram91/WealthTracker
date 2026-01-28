using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using WealthTracker.Helper;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddMemoryCache();

// Register DataProcessHelper as a singleton and assign the same instance to the static Instance.
// This guarantees all controllers (DI or static access) use the same object and shared cache.
builder.Services.AddSingleton<DataProcessHelper>(sp =>
{
    var mem = sp.GetRequiredService<IMemoryCache>();
    var helper = new DataProcessHelper(mem);
    DataProcessHelper.Instance = helper;
    return helper;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
