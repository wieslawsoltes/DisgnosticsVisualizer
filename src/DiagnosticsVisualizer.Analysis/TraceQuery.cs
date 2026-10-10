using System.Globalization;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed class TraceQueryException(string message, int position) : FormatException($"{message} at character {position + 1}.")
{
    public int Position { get; } = position;
}

/// <summary>A bounded predicate parser: implicit AND, OR, NOT, parentheses, quoted text,
/// name/subsystem/thread/object/id qualifiers, and duration comparisons with ns/us/ms/s units.</summary>
public static class TraceQuery
{
    public static Func<TraceEvent, bool> Compile(string? text, TraceView trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (string.IsNullOrWhiteSpace(text)) return static _ => true;
        if (text.Length > 4096) throw new TraceQueryException("Query exceeds 4096 characters", 4096);
        return new Parser(text, trace).Parse();
    }
    private enum Kind { Word, String, Left, Right, And, Or, Not, Compare, End }
    private readonly record struct Token(Kind Kind, string Text, int Position);
    private sealed class Parser
    {
        private readonly List<Token> _tokens = [];
        private readonly TraceView _trace;
        private int _index, _depth;
        public Parser(string text, TraceView trace)
        {
            _trace = trace;
            for (var i = 0; i < text.Length;)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                if (_tokens.Count >= 256) throw new TraceQueryException("Too many query terms", i);
                var start = i; var c = text[i++];
                if (c is '(' or ')') { _tokens.Add(new(c == '(' ? Kind.Left : Kind.Right, c.ToString(), start)); continue; }
                if (c == '"')
                {
                    var value = new System.Text.StringBuilder(); var closed = false;
                    while (i < text.Length)
                    {
                        c = text[i++]; if (c == '"') { closed = true; break; }
                        if (c == '\\' && i < text.Length) c = text[i++];
                        value.Append(c);
                    }
                    if (!closed) throw new TraceQueryException("Unterminated quoted string", start);
                    _tokens.Add(new(Kind.String, value.ToString(), start)); continue;
                }
                if (c is ':' or '=' or '<' or '>' or '~' or '!')
                {
                    if (i < text.Length && text[i] == '=') i++;
                    var op = text[start..i]; _tokens.Add(new(op == "!" ? Kind.Not : Kind.Compare, op, start)); continue;
                }
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && "()\":=<>~!".IndexOf(text[i]) < 0) i++;
                var word = text[start..i];
                _tokens.Add(new(word.ToLowerInvariant() switch { "and" or "&&" => Kind.And, "or" or "||" => Kind.Or, "not" => Kind.Not, _ => Kind.Word }, word, start));
            }
            _tokens.Add(new(Kind.End, "", text.Length));
        }
        private Token Current => _tokens[_index];
        public Func<TraceEvent, bool> Parse()
        {
            var predicate = Or();
            if (Current.Kind != Kind.End) throw Error("Unexpected token");
            return predicate;
        }
        private Func<TraceEvent, bool> Or()
        {
            var left = And();
            while (Current.Kind == Kind.Or) { _index++; var previous = left; var right = And(); left = e => previous(e) || right(e); }
            return left;
        }
        private Func<TraceEvent, bool> And()
        {
            var left = Unary();
            while (Current.Kind is Kind.And or Kind.Word or Kind.String or Kind.Not or Kind.Left)
            {
                if (Current.Kind == Kind.And) _index++;
                var previous = left; var right = Unary(); left = e => previous(e) && right(e);
            }
            return left;
        }
        private Func<TraceEvent, bool> Unary()
        {
            if (++_depth > 16) throw Error("Query nesting exceeds 16 levels");
            try
            {
                if (Current.Kind == Kind.Not) { _index++; var inner = Unary(); return e => !inner(e); }
                if (Current.Kind == Kind.Left)
                {
                    _index++; var inner = Or(); if (Current.Kind != Kind.Right) throw Error("Expected closing parenthesis"); _index++; return inner;
                }
                if (Current.Kind is not (Kind.Word or Kind.String)) throw Error("Expected a search term");
                var key = Current; _index++;
                if (Current.Kind != Kind.Compare)
                    return e => _trace.Name(e).Contains(key.Text, StringComparison.OrdinalIgnoreCase) ||
                        e.Subsystem.ToString().Contains(key.Text, StringComparison.OrdinalIgnoreCase) ||
                        (long.TryParse(key.Text, out var id) && (e.Id == id || e.ObjectId == id));
                var op = Current.Text; _index++;
                if (Current.Kind is not (Kind.Word or Kind.String)) throw Error("Expected a qualifier value");
                var value = Current; _index++;
                return Qualified(key, op, value);
            }
            finally { _depth--; }
        }
        private Func<TraceEvent, bool> Qualified(Token key, string op, Token value)
        {
            switch (key.Text.ToLowerInvariant())
            {
                case "name": case "operation": return Text(e => _trace.Name(e));
                case "subsystem": case "system": return Text(e => e.Subsystem.ToString());
                case "kind": return Text(e => e.Kind.ToString());
                case "thread": return Identity(e => e.ThreadId, Integer(value));
                case "object": return Identity(e => e.ObjectId, Integer(value));
                case "id": case "event": return Identity(e => e.Id, Integer(value));
                case "parent": return Identity(e => e.ParentId, Integer(value));
                case "duration": case "dur":
                    var ticks = Duration(value) * _trace.Frequency / 1000;
                    return Number(e => e.Duration, ticks);
                default: throw new TraceQueryException($"Unknown qualifier '{key.Text}'", key.Position);
            }
            Func<TraceEvent, bool> Text(Func<TraceEvent, string> get)
            {
                return op switch
                {
                    ":" or "~" => e => get(e).Contains(value.Text, StringComparison.OrdinalIgnoreCase),
                    "=" or "==" => e => get(e).Equals(value.Text, StringComparison.OrdinalIgnoreCase),
                    "!=" => e => !get(e).Equals(value.Text, StringComparison.OrdinalIgnoreCase),
                    _ => throw new TraceQueryException("Text qualifiers support :, ~, = and !=", key.Position)
                };
            }
            Func<TraceEvent, bool> Identity(Func<TraceEvent, long> get, long expected) => op switch
            {
                ":" or "=" or "==" => e => get(e) == expected,
                "!=" => e => get(e) != expected,
                ">" => e => get(e) > expected, ">=" => e => get(e) >= expected,
                "<" => e => get(e) < expected, "<=" => e => get(e) <= expected,
                _ => throw new TraceQueryException("Invalid identifier comparison", key.Position)
            };
            Func<TraceEvent, bool> Number(Func<TraceEvent, long> get, double expected) => op switch
            {
                ":" or "=" or "==" => e => get(e) == expected,
                "!=" => e => get(e) != expected,
                ">" => e => get(e) > expected, ">=" => e => get(e) >= expected,
                "<" => e => get(e) < expected, "<=" => e => get(e) <= expected,
                _ => throw new TraceQueryException("Invalid numeric comparison", key.Position)
            };
        }
        private static long Integer(Token token) => long.TryParse(token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw new TraceQueryException("Expected an integer identifier", token.Position);
        private static double Duration(Token token)
        {
            var text = token.Text; var factor = 1d;
            foreach (var (unit, multiplier) in new[] { ("ns", .000001), ("us", .001), ("µs", .001), ("ms", 1d), ("s", 1000d) })
                if (text.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) { text = text[..^unit.Length]; factor = multiplier; break; }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0 || value > 1e12)
                throw new TraceQueryException("Expected a finite nonnegative duration", token.Position);
            return value * factor;
        }
        private TraceQueryException Error(string message) => new(message, Current.Position);
    }
}
