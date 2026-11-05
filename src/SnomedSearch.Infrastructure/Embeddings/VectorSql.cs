using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SnomedSearch.Infrastructure.Embeddings
{
    /// <summary>Helpers for building pgvector SQL safely.</summary>
    public static class VectorSql
    {
        private static readonly Regex ModelKeyPattern = new(@"^[A-Za-z0-9:._/\-]{1,100}$", RegexOptions.Compiled);

        /// <summary>
        /// The model key appears as a literal in partial-index predicates (so the planner can match the index),
        /// which is why it is restricted to a safe character set.
        /// </summary>
        public static string ValidateModelKey(string key)
        {
            if (!ModelKeyPattern.IsMatch(key))
                throw new ArgumentException("Model key may contain only letters, digits and : . _ / - (max 100).", nameof(key));
            return key;
        }

        public static string Literal(string validatedKey) => "'" + validatedKey.Replace("'", "''") + "'";

        /// <summary>Index name derived from the model key, stable and within the 63-byte identifier limit.</summary>
        public static string IndexName(string validatedKey)
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(validatedKey)))[..12].ToLowerInvariant();
            return "ix_desc_emb_hnsw_" + hash;
        }

        /// <summary>pgvector text format: [1,2,3]. Invariant culture so decimal points never become commas.</summary>
        public static string ToText(float[] v)
        {
            var sb = new StringBuilder(v.Length * 10 + 2).Append('[');
            for (var i = 0; i < v.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(v[i].ToString("R", CultureInfo.InvariantCulture));
            }
            return sb.Append(']').ToString();
        }
    }
}
