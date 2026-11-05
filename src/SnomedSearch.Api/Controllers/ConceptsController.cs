using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using SnomedSearch.Core.Entities;
using SnomedSearch.Core.Interfaces;
using SnomedSearch.Core.Models;

namespace SnomedSearch.Api.Controllers
{
    /// <summary>Query parameters shared by search and suggest.</summary>
    public class SearchParameters
    {
        /// <summary>Text to search for. Letters and digits only are used; other characters separate words.</summary>
        [FromQuery(Name = "q")]
        [Required, StringLength(200, MinimumLength = 1)]
        [RegularExpression(@".*\S.*", ErrorMessage = "The query must contain a non-space character.")]
        public string Q { get; set; } = "";

        /// <summary>Restrict to these semantic tags, for example tags=finding&amp;tags=disorder. Default: all configured tags.</summary>
        [FromQuery(Name = "tags")]
        [MaxLength(20)]
        public string[]? Tags { get; set; }

        /// <summary>Restrict to this concept and its IS-A descendants, e.g. a subtree such as all heart disorders.</summary>
        [FromQuery(Name = "within")]
        [Range(1, long.MaxValue)]
        public long? Within { get; set; }
    }

    public class SearchRequestParameters : SearchParameters
    {
        /// <summary>lexical (default), semantic or hybrid.</summary>
        [FromQuery(Name = "mode")]
        public SearchMode Mode { get; set; } = SearchMode.Lexical;

        [FromQuery(Name = "limit")]
        [Range(1, 100)]
        public int Limit { get; set; } = 20;

        /// <summary>1-based page number. For deep paging use the cursor instead.</summary>
        [FromQuery(Name = "page")]
        [Range(1, 100000)]
        public int Page { get; set; } = 1;

        /// <summary>Opaque token from a previous response's nextCursor. Takes precedence over page.</summary>
        [FromQuery(Name = "cursor")]
        [StringLength(512)]
        public string? Cursor { get; set; }
    }

    public class SuggestParameters : SearchParameters
    {
        [FromQuery(Name = "limit")]
        [Range(1, 20)]
        public int Limit { get; set; } = 8;
    }

    [ApiController]
    [Route("api/concepts")]
    public class ConceptsController : ControllerBase
    {
        private const int MaxOffset = 10_000;
        private readonly ISnomedRepository _repository;

        public ConceptsController(ISnomedRepository repository)
        {
            _repository = repository;
        }

        /// <summary>Search concepts by text.</summary>
        /// <remarks>
        /// Lexical mode: every word must match the start of a word in a term; results are ranked exact term,
        /// then term starting with the query, then the rest. If no word matches at all, a typo-tolerant match runs.
        /// Page with <c>cursor</c> (keyset) for deep results; <c>page</c> is capped.
        /// </remarks>
        [HttpGet("search")]
        [ProducesResponseType(typeof(ConceptSearchResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status501NotImplemented)]
        public async Task<IActionResult> Search([FromQuery] SearchRequestParameters p, CancellationToken ct)
        {
            var bad = ValidateTags(p.Tags);
            if (bad is not null) return bad;

            var offset = (p.Page - 1) * p.Limit;
            if (p.Cursor is null && offset > MaxOffset)
            {
                return Problem(
                    title: "Page is too deep",
                    detail: $"Offset paging is limited to {MaxOffset} results. Use the nextCursor value from the previous response.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                var result = await _repository.SearchAsync(
                    new ConceptSearchQuery(p.Q, p.Mode, p.Tags, p.Limit, offset, p.Cursor, p.Within), ct);
                return Ok(result);
            }
            catch (InvalidSearchRequestException e)
            {
                return Problem(title: "Invalid request", detail: e.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (NotSupportedException e)
            {
                return Problem(title: "Search mode not available", detail: e.Message, statusCode: StatusCodes.Status501NotImplemented);
            }
        }

        /// <summary>Suggestions while typing. No total count, no typo fallback, up to 20 results.</summary>
        [HttpGet("suggest")]
        [ProducesResponseType(typeof(List<ConceptSummary>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Suggest([FromQuery] SuggestParameters p, CancellationToken ct)
        {
            var bad = ValidateTags(p.Tags);
            if (bad is not null) return bad;
            return Ok(await _repository.SuggestAsync(p.Q, p.Tags, p.Limit, ct, p.Within));
        }

        /// <summary>Concept details: names, definition, parents and child count.</summary>
        [HttpGet("{conceptId:long}")]
        [ProducesResponseType(typeof(Concept), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetConcept(long conceptId, CancellationToken ct)
        {
            var concept = await _repository.GetConceptDetailsAsync(conceptId, ct);
            return concept is null ? NotFoundProblem(conceptId) : Ok(concept);
        }

        /// <summary>Direct children (IS-A), ordered by preferred term.</summary>
        [HttpGet("{conceptId:long}/children")]
        [ProducesResponseType(typeof(HierarchyResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetChildren(long conceptId, [FromQuery, Range(1, 500)] int limit = 50, CancellationToken ct = default)
        {
            var result = await _repository.GetChildrenAsync(conceptId, limit, ct);
            return result is null ? NotFoundProblem(conceptId) : Ok(result);
        }

        /// <summary>Direct parents (IS-A).</summary>
        [HttpGet("{conceptId:long}/parents")]
        [ProducesResponseType(typeof(HierarchyResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetParents(long conceptId, CancellationToken ct)
        {
            var result = await _repository.GetParentsAsync(conceptId, ct);
            return result is null ? NotFoundProblem(conceptId) : Ok(result);
        }

        /// <summary>All IS-A descendants (transitive), ordered by concept id. Use for value sets.</summary>
        /// <remarks>Does not include the concept itself. Page with <c>after</c> set to the previous response's nextAfter.</remarks>
        [HttpGet("{conceptId:long}/descendants")]
        [ProducesResponseType(typeof(DescendantsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetDescendants(
            long conceptId,
            [FromQuery, Range(1, 1000)] int limit = 100,
            [FromQuery, Range(1, long.MaxValue)] long? after = null,
            [FromQuery, MaxLength(20)] string[]? tags = null,
            CancellationToken ct = default)
        {
            var bad = ValidateTags(tags);
            if (bad is not null) return bad;
            var result = await _repository.GetDescendantsAsync(conceptId, tags, limit, after, ct);
            return result is null ? NotFoundProblem(conceptId) : Ok(result);
        }

        /// <summary>All IS-A ancestors (transitive), nearest first, with their shortest distance.</summary>
        [HttpGet("{conceptId:long}/ancestors")]
        [ProducesResponseType(typeof(List<ConceptSummary>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetAncestors(long conceptId, CancellationToken ct)
        {
            var result = await _repository.GetAncestorsAsync(conceptId, ct);
            return result is null ? NotFoundProblem(conceptId) : Ok(result);
        }

        /// <summary>Active synonyms, alphabetical.</summary>
        [HttpGet("{conceptId:long}/synonyms")]
        [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetSynonyms(long conceptId, CancellationToken ct)
        {
            var result = await _repository.GetSynonymsAsync(conceptId, ct);
            return result is null ? NotFoundProblem(conceptId) : Ok(result);
        }

        /// <summary>Number of concepts per searchable semantic tag.</summary>
        [HttpGet("semantic-tags")]
        [ProducesResponseType(typeof(Dictionary<string, int>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSemanticTagStats(CancellationToken ct) =>
            Ok(await _repository.GetSemanticTagStatsAsync(ct));

        private IActionResult NotFoundProblem(long conceptId) =>
            Problem(title: "Concept not found", detail: $"No active concept with id {conceptId}.", statusCode: StatusCodes.Status404NotFound);

        private IActionResult? ValidateTags(string[]? tags)
        {
            if (tags is null) return null;
            if (tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 100))
            {
                ModelState.AddModelError("tags", "Each tag must be 1 to 100 characters.");
                return ValidationProblem(ModelState);
            }
            return null;
        }
    }
}
