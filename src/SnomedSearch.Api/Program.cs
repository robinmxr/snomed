using Microsoft.EntityFrameworkCore;
using SnomedSearch.Core.Interfaces;
using SnomedSearch.Infrastructure.Data;
using dotenv.net;
using System;

DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure SNOMED Repository (fails at startup if required settings are missing)
var dbSettings = DatabaseSettings.FromEnvironment();
string connString = dbSettings.ToConnectionString();

builder.Services.AddDbContext<SnomedDbContext>(options =>
    options.UseNpgsql(connString));

builder.Services.AddSingleton(builder.Configuration.GetSection("Search").Get<LexicalSearchOptions>() ?? new LexicalSearchOptions());
builder.Services.AddScoped<ISnomedRepository, SnomedRepository>();

var app = builder.Build();

// Unhandled errors become application/problem+json without leaking internals.
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();

app.Run();
