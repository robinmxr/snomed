using Microsoft.EntityFrameworkCore;
using SnomedSearch.Core.Interfaces;
using SnomedSearch.Infrastructure.Data;
using SnomedSearch.Infrastructure.Embeddings;
using dotenv.net;
using System;

// Values already in the environment win over the .env file.
DotEnv.Load(new DotEnvOptions(ignoreExceptions: true, overwriteExistingVars: false, probeForEnv: true, probeLevelsToSearch: 4));

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

var lexicalOptions = builder.Configuration.GetSection("Search").Get<LexicalSearchOptions>() ?? new LexicalSearchOptions();
var embeddingOptions = builder.Configuration.GetSection("Embedding").Get<EmbeddingOptions>() ?? new EmbeddingOptions();
var semanticOptions = builder.Configuration.GetSection("Semantic").Get<SemanticOptions>() ?? new SemanticOptions();
var hybridOptions = builder.Configuration.GetSection("Hybrid").Get<HybridOptions>() ?? new HybridOptions();

// Semantic and hybrid search are optional: without an embedding server they return 501.
if (embeddingOptions.IsConfigured)
{
    builder.Services.AddSingleton(embeddingOptions);
    builder.Services.AddSingleton(new QueryEmbeddingCache(semanticOptions.QueryCacheSize));
    builder.Services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>((http, sp) =>
        new OllamaEmbeddingClient(http, embeddingOptions));
    builder.Services.AddScoped(sp => new SemanticSearch(
        sp.GetRequiredService<SnomedDbContext>(), sp.GetRequiredService<IEmbeddingClient>(),
        embeddingOptions, semanticOptions, sp.GetRequiredService<QueryEmbeddingCache>()));
}

builder.Services.AddScoped<ISnomedRepository>(sp => new SnomedRepository(
    sp.GetRequiredService<SnomedDbContext>(), lexicalOptions, sp.GetService<SemanticSearch>(), hybridOptions));

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

// Lets the test project start the app with WebApplicationFactory.
public partial class Program { }
