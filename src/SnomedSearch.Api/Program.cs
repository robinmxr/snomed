using Microsoft.EntityFrameworkCore;
using SnomedSearch.Core.Interfaces;
using SnomedSearch.Infrastructure.Data;
using SnomedSearch.Infrastructure.Services;
using dotenv.net;
using System;

DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure SNOMED Repository (fails at startup if required settings are missing)
var dbSettings = DatabaseSettings.FromEnvironment();
string connString = dbSettings.ToConnectionString();

builder.Services.AddDbContext<SnomedDbContext>(options =>
    options.UseNpgsql(connString));

builder.Services.AddScoped<ISnomedRepository, SnomedRepository>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();

app.Run();
