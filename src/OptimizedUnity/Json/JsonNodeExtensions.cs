using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptimizedUnity.Json;

/// <summary>Null-safe accessors over untrusted JSON: a missing key or wrong type yields null, never a throw.</summary>
internal static class JsonNodeExtensions
{
	static readonly JsonWriterOptions CompactWriter = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		Indented = false,
	};

	static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true };

	extension(JsonNode)
	{
		/// <summary>Parse `text` as JSON, tolerating leading banners and trailing noise.</summary>
		public static JsonNode? ParseLenient(string? text)
		{
			if (string.IsNullOrWhiteSpace(text)) return null;
			var s = text.Trim();
			if (TryParseExact(s, out var whole)) return whole;
			for (var i = 0; i < s.Length; i++)
			{
				if (s[i] is not ('{' or '[')) continue;
				var decoded = DecodeAt(s, i);
				if (decoded is not null) return decoded;
			}
			return null;
		}
	}

	extension(JsonNode? node)
	{
		public bool IsScalar() => node is null or JsonValue;
		public bool IsFlat() => node is JsonObject obj && obj.All(pair => pair.Value.IsScalar());

		public JsonNode? Get(string key) =>
			node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

		public bool Has(string key) => node is JsonObject obj && obj.ContainsKey(key);

		/// <summary>Case-insensitive lookup, for payloads that mix `Results` and `results`.</summary>
		public JsonNode? GetAny(string key)
		{
			if (node is not JsonObject obj) return null;
			if (obj.TryGetPropertyValue(key, out var exact)) return exact;
			foreach (var (name, value) in obj)
				if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return value;
			return null;
		}

		public JsonValueKind Kind() => node?.GetValueKind() ?? JsonValueKind.Null;

		public string? Str() =>
			node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

		public bool? Bool() => node.Kind() switch
		{
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			_ => null,
		};

		public bool TryNumber(out double number)
		{
			number = 0;
			if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return false;
			if (value.TryGetValue(out JsonElement element)) return element.TryGetDouble(out number);
			return double.TryParse(value.Compact(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
		}

		/// <summary>Integral number value, or null (JS Number.isInteger).</summary>
		public long? Int() =>
			node.TryNumber(out var number) && Math.Floor(number) == number && !double.IsInfinity(number)
				? (long)number
				: null;

		/// <summary>JavaScript truthiness.</summary>
		public bool Truthy() => node.Kind() switch
		{
			JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
			JsonValueKind.String => node.Str()!.Length > 0,
			JsonValueKind.Number => node.TryNumber(out var n) && n != 0 && !double.IsNaN(n),
			_ => true,
		};

		public bool IsEmpty() => node switch
		{
			null => true,
			JsonArray array => array.Count == 0,
			JsonObject obj => obj.Count == 0,
			_ => node.Kind() == JsonValueKind.Null || node.Str() is "",
		};

		public IEnumerable<JsonNode?> Items() => node is JsonArray array ? array : [];

		/// <summary>Compact JSON without escaping non-ASCII, like JSON.stringify.</summary>
		public string Compact()
		{
			if (node is null) return "null";
			using var buffer = new MemoryStream();
			using (var writer = new Utf8JsonWriter(buffer, CompactWriter)) node.WriteTo(writer);
			return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
		}

		/// <summary>The CLI's `--json` envelope: {success, data|errors}.</summary>
		public bool IsEnvelope() =>
			node is JsonObject obj && obj.ContainsKey("success") && (obj.ContainsKey("data") || obj.ContainsKey("errors"));
	}

	/// <summary>First present, non-null value among keys (JS `a ?? b ?? c`).</summary>
	/// <remarks>A classic extension: `params` in an extension block trips a nullability bug (CS8620).</remarks>
	public static JsonNode? First(this JsonNode? node, params ReadOnlySpan<string> keys)
	{
		foreach (var key in keys)
		{
			var value = node.Get(key);
			if (value is not null) return value;
		}
		return null;
	}

	static bool TryParseExact(string text, out JsonNode? node)
	{
		try
		{
			node = JsonNode.Parse(text, documentOptions: Lenient);
			return node is not null;
		}
		catch (JsonException)
		{
			node = null;
			return false;
		}
	}

	/// <summary>Parse one object/array starting at `start`, ignoring trailing text.</summary>
	static JsonNode? DecodeAt(string text, int start)
	{
		var depth = 0;
		var inString = false;
		var escaped = false;
		for (var j = start; j < text.Length; j++)
		{
			var ch = text[j];
			if (inString)
			{
				if (escaped) escaped = false;
				else if (ch == '\\') escaped = true;
				else if (ch == '"') inString = false;
				continue;
			}
			if (ch == '"')
			{
				inString = true;
				continue;
			}
			if (ch is '{' or '[') depth++;
			else if (ch is '}' or ']')
			{
				depth--;
				if (depth == 0) return TryParseExact(text[start..(j + 1)], out var node) ? node : null;
			}
		}
		return null;
	}
}
