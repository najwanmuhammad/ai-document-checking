using HinoDocumentAI.Service.Services;

var builder = WebApplication.CreateBuilder(args);

// aktifkan ini saat deploy ke server production. Butuh package: Microsoft.Extensions.Hosting.WindowsServices
// builder.Host.UseWindowsService();

// Add services to the container.

builder.Services.AddControllers();
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


// TODO: karena AI Service ini hanya dipanggil secara internal (localhost)
// oleh Invoice Portal di server yang sama, pastikan saat deploy production
// Kestrel di-bind HANYA ke 127.0.0.1 (bukan 0.0.0.0), lewat konfigurasi
// "Kestrel:Endpoints:Http:Url": "http://127.0.0.1:PORT" di appsettings.json
// — lihat PRD Bagian 11, prinsip desain isolasi jaringan.

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

app.Run();
