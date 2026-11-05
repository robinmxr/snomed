using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using dotenv.net;
using SnomedSearch.Core.Entities;
using SnomedSearch.Core.Interfaces;
using SnomedSearch.Infrastructure.Data;
using SnomedSearch.Infrastructure.Services;

namespace SnomedSearch.ConsoleApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            DotEnv.Load();
            
            string connString = DatabaseSettings.FromEnvironment().ToConnectionString();

            var optionsBuilder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<SnomedDbContext>();
            optionsBuilder.UseNpgsql(connString);
            using var dbContext = new SnomedDbContext(optionsBuilder.Options);

            ISnomedRepository repository = new SnomedRepository(dbContext);

            Console.WriteLine("========================================");
            Console.WriteLine(" SNOMED CT Chief Complaint Search (.NET)");
            Console.WriteLine("========================================");
            Console.WriteLine("Type 'exit' to quit.");

            while (true)
            {
                Console.Write("\nEnter search query: ");
                string query = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(query) || query.ToLower() == "exit")
                    break;

                Console.WriteLine($"Searching for '{query}'...");
                try
                {
                    var results = await repository.SearchChiefComplaintsAsync(query);

                    if (results.Count == 0)
                    {
                        Console.WriteLine("No results found.");
                        continue;
                    }

                    Console.WriteLine($"\nFound {results.Count} results:");
                    Console.WriteLine(new string('-', 30));
                    for (int i = 0; i < results.Count; i++)
                    {
                        var res = results[i];
                        Console.WriteLine($"{i + 1}. {res.PreferredTerm}");
                    }
                    Console.WriteLine(new string('-', 30));

                    if (results.Count > 0)
                    {
                        Console.Write("\nEnter number for details (or Enter to search again): ");
                        string input = Console.ReadLine();
                        if (int.TryParse(input, out int index) && index > 0 && index <= results.Count)
                        {
                            await DisplayDetails(repository, results[index - 1].ConceptId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
        }

        static async Task DisplayDetails(ISnomedRepository repository, long conceptId)
        {
            var details = await repository.GetConceptDetailsAsync(conceptId);
            if (details == null)
            {
                Console.WriteLine("Concept not found.");
                return;
            }

            Console.WriteLine("\n--- CONCEPT DETAILS ---");
            Console.WriteLine($"ID: {details.ConceptId}");
            Console.WriteLine($"FSN: {details.Fsn}");
            Console.WriteLine($"Preferred Term: {details.PreferredTerm}");
            Console.WriteLine($"Semantic Tag: {details.SemanticTag}");
            Console.WriteLine($"Synonyms: {string.Join(", ", details.Synonyms)}");
            
            if (!string.IsNullOrEmpty(details.Definition))
                Console.WriteLine($"Definition: {details.Definition}");

            if (details.Parents.Count > 0)
            {
                Console.WriteLine("Parents:");
                foreach (var p in details.Parents)
                    Console.WriteLine($"  - [{p.ConceptId}] {p.PreferredTerm}");
            }

            Console.WriteLine($"Children Count: {details.ChildrenCount}");
            Console.WriteLine("-----------------------\n");
        }
    }
}
