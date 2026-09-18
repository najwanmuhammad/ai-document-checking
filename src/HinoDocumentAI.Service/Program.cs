using HinoDocumentAI.Service.Services;

var builder = WebApplication.CreateBuilder(args);

// AI Service berjalan sebagai proses internal/Windows Service. Hindari default
// Windows Event Log provider karena service account belum tentu boleh membuat
// event source; logging tetap tersedia melalui stdout/debug collector.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// aktifkan ini saat deploy ke server production. Butuh package: Microsoft.Extensions.Hosting.WindowsServices
// builder.Host.UseWindowsService();

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                System.Text.Json.JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
    });
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Services AI — registrasi sebagai Singleton untuk OcrService (model PaddleOCR mahal untuk di-load ulang tiap request), Scoped untuk yang
// lain karena tidak menyimpan state berat.
builder.Services.AddSingleton<IOcrService, OcrService>();
builder.Services.AddScoped<ICleaningService, CleaningService>();
builder.Services.AddScoped<IMatchingService, MatchingService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


// AI Service hanya dipanggil secara internal (localhost) oleh Invoice Portal.
// Konfigurasi production mengikat Kestrel ke 127.0.0.1 (bukan 0.0.0.0) lewat
// "Kestrel:Endpoints:Http:Url": "http://127.0.0.1:PORT" di appsettings.json
// — lihat PRD Bagian 11, prinsip desain isolasi jaringan.

app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

app.Run();
