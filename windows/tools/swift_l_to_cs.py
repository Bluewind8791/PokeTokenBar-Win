"""Converts Sources/PokeTokenBar/Core/Localization.swift (struct L) into C# members.

Usage: python swift_l_to_cs.py <Localization.swift> <Localization.cs> <Localization.hand.txt>

Members whose body is a single `t(...)` call of string literals are converted mechanically.
Every other member is looked up by Swift name in the hand-ported file (blocks delimited by
`// PORT <swiftName>` ... `// END <swiftName>`, where `TSWIFT(n)` expands to the member's n-th
`t(...)` call, converted); members missing there are emitted as a `#error` stub so the build fails
until they are ported. The hand file also carries English versions of Korean doc comments
(`// DOC <swiftName>` blocks) and `// MARK:` titles (`MARK: <ko> => <en>` lines).
Korean doc comments without a DOC block are dropped (listed on stdout).
"""
import re
import sys

# ---------------------------------------------------------------- Swift lexing


def read_swift_string(src, i):
    """src[i] == '"'. Returns (end, parts) with parts = [('text', s) | ('interp', expr)]."""
    multiline = src.startswith('"""', i)
    if multiline:
        start = src.index('\n', i) + 1
        close = src.index('"""', start)
        while src[close - 1] == '\\' and src[close - 2] != '\\':  # escaped quote run; not used here
            close = src.index('"""', close + 1)
        body = src[start:close]
        lines = body.split('\n')
        indent = lines[-1]
        assert indent.strip() == '', 'closing """ must be on its own line'
        content_lines = [ln[len(indent):] if ln.startswith(indent) else ln.lstrip() for ln in lines[:-1]]
        raw = '\n'.join(content_lines)
        end = close + 3
    else:
        j = i + 1
        while True:
            c = src[j]
            if c == '\\':
                if src[j + 1] == '(':
                    j = skip_parens(src, j + 1)
                    continue
                j += 2
                continue
            if c == '"':
                break
            if c == '\n':
                raise ValueError('newline in single-line string at %d' % i)
            j += 1
        raw = src[i + 1:j]
        end = j + 1
    return end, unescape(raw)


def skip_parens(src, j):
    """src[j] == '('. Returns index after the matching ')', skipping nested strings."""
    depth = 0
    while True:
        c = src[j]
        if c == '"':
            j, _ = read_swift_string(src, j)
            continue
        if c == '(':
            depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0:
                return j + 1
        j += 1


def unescape(raw):
    parts, buf, k = [], [], 0
    simple = {'n': '\n', 't': '\t', 'r': '\r', '0': '\0', '"': '"', "'": "'", '\\': '\\'}
    while k < len(raw):
        c = raw[k]
        if c != '\\':
            buf.append(c)
            k += 1
            continue
        n = raw[k + 1]
        if n in simple:
            buf.append(simple[n])
            k += 2
        elif n == 'u':
            close = raw.index('}', k)
            buf.append(chr(int(raw[k + 3:close], 16)))
            k = close + 1
        elif n == '\n':  # line continuation in multiline strings
            k += 2
        elif n == '(':
            end = skip_parens(raw, k + 1)
            if buf:
                parts.append(('text', ''.join(buf)))
                buf = []
            parts.append(('interp', raw[k + 2:end - 1]))
            k = end
        else:
            raise ValueError('unknown escape \\%s' % n)
    if buf:
        parts.append(('text', ''.join(buf)))
    return parts


# ---------------------------------------------------------------- C# emission


def cs_escape_text(s, interpolated):
    out = []
    for c in s:
        if c == '\\':
            out.append('\\\\')
        elif c == '"':
            out.append('\\"')
        elif c == '\n':
            out.append('\\n')
        elif c == '\t':
            out.append('\\t')
        elif c == '\r':
            out.append('\\r')
        elif c == '\0':
            out.append('\\0')
        elif interpolated and c in '{}':
            out.append(c * 2)
        else:
            out.append(c)
    return ''.join(out)


PARTICLE = re.compile(r'^KoreanParticle\.(subject|object|direction)\.attach\(to:\s*(\w+)\)$')


def cs_expr(expr):
    expr = expr.strip()
    if re.fullmatch(r'[a-z]\w*', expr):
        return expr
    m = PARTICLE.match(expr)
    if m:
        return 'KoreanParticle.%s.Attach(%s)' % (m.group(1).capitalize(), m.group(2))
    raise ValueError('unsupported interpolation: ' + expr)


def cs_string(parts):
    interp = any(k == 'interp' for k, _ in parts)
    body = ''.join(cs_escape_text(v, interp) if k == 'text' else '{' + cs_expr(v) + '}' for k, v in parts)
    return ('$"' if interp else '"') + body + '"'


# ---------------------------------------------------------------- member splitting


def find_struct_body(src):
    start = src.index('struct L {')
    return start + len('struct L {'), find_close(src, src.index('{', start))


def find_close(src, i):
    """src[i] == '{'. Index of matching '}', skipping strings and comments."""
    depth = 0
    while True:
        c = src[i]
        if c == '"':
            i, _ = read_swift_string(src, i)
            continue
        if src.startswith('//', i):
            i = src.index('\n', i)
            continue
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                return i
        i += 1


def split_items(body):
    """Yields ('comment', line) | ('blank',) | ('member', text)."""
    i, n = 0, len(body)
    while i < n:
        nl = body.find('\n', i)
        if nl == -1:
            nl = n
        line = body[i:nl]
        stripped = line.strip()
        if not stripped:
            yield ('blank',)
            i = nl + 1
            continue
        if stripped.startswith('//'):
            yield ('comment', stripped)
            i = nl + 1
            continue
        # declaration: runs to the matching close brace of its first '{'
        brace = body.index('{', i)
        close = find_close(body, brace)
        end = body.find('\n', close)
        end = n if end == -1 else end
        yield ('member', body[i:end].strip())
        i = end + 1


HEADER_VAR = re.compile(r'^var (\w+): String \{(.*)\}$', re.S)
HEADER_FUNC = re.compile(r'^func (\w+)\((.*?)\) -> (String\??) \{(.*)\}$', re.S)

TYPE_MAP = {'String': 'string', 'Int': 'int', 'Double': 'double', 'Bool': 'bool', 'UInt64': 'ulong',
            'TimeInterval': 'double', 'String?': 'string?', 'Int?': 'int?'}


def member_name(text):
    m = re.match(r'^(?:var|func) (\w+)', text)
    return m.group(1) if m else None


def pascal(name):
    return name[0].upper() + name[1:]


def parse_t_call(body):
    """If body is exactly `t(<8 string literals>)`, returns the 8 part lists."""
    body = body.strip()
    if not body.startswith('t(') or skip_parens(body, 1) != len(body):
        return None
    args, i = [], 2
    while True:
        while body[i] in ' \n,':
            i += 1
        if body[i] == ')':
            break
        if body[i] != '"':
            return None
        i, parts = read_swift_string(body, i)
        args.append(parts)
    return args if len(args) == 8 else None


def convert_params(params):
    out = []
    for p in [p.strip() for p in params.split(',') if p.strip()]:
        m = re.fullmatch(r'(?:(\w+) )?(\w+): (\S+)', p)
        if not m or m.group(3) not in TYPE_MAP:
            return None
        out.append('%s %s' % (TYPE_MAP[m.group(3)], m.group(2)))
    return ', '.join(out)


def convert_simple(text, indent):
    m = HEADER_VAR.match(text)
    if m:
        args = parse_t_call(m.group(2))
        if args is None:
            return None
        head = 'public string %s =>' % pascal(m.group(1))
    else:
        m = HEADER_FUNC.match(text)
        if not m or m.group(3) != 'String':
            return None
        args = parse_t_call(m.group(4))
        params = convert_params(m.group(2))
        if args is None or params is None:
            return None
        head = 'public string %s(%s) =>' % (pascal(m.group(1)), params)
    strings = [cs_string(a) for a in args]
    one = '%s%s T(%s);' % (indent, head, ', '.join(strings))
    if len(one) <= 180:
        return one
    sep = ',\n' + indent + '    '
    return '%s%s T(\n%s    %s);' % (indent, head, indent, sep.join(strings))


def find_t_calls(text):
    """Every `t(...)` call in a member's Swift source, in order, as converted C# `T(...)` text."""
    calls, i = [], 0
    while i < len(text):
        c = text[i]
        if c == '"':
            i, _ = read_swift_string(text, i)
            continue
        if text.startswith('//', i):
            i = text.index('\n', i)
            continue
        if text.startswith('t(', i) and not (text[i - 1].isalnum() or text[i - 1] in '_.'):
            end = skip_parens(text, i + 1)
            args = parse_t_call(text[i:end])
            if args is None:
                raise ValueError('t(...) with non-literal arguments: ' + text[i:end][:80])
            calls.append(format_t([cs_string(a) for a in args], 12))
            i = end
            continue
        i += 1
    return calls


def format_t(strings, col):
    one = 'T(%s)' % ', '.join(strings)
    if len(one) + col <= 180:
        return one
    pad = ' ' * (col + 4)
    return 'T(\n%s%s)' % (pad, (',\n' + pad).join(strings))


def expand_tswift(block, swift_text, key):
    calls = find_t_calls(swift_text)
    used = set()

    def sub(m):
        n = int(m.group(1))
        used.add(n)
        return calls[n]
    out = re.sub(r'TSWIFT\((\d+)\)', sub, block)
    if used != set(range(len(calls))):
        raise ValueError('%s: TSWIFT uses %s but Swift has %d t() calls' % (key, sorted(used), len(calls)))
    return out


HEADER = """// <auto-generated>
// Generated from Sources/PokeTokenBar/Core/Localization.swift by tools/swift_l_to_cs.py
// (+ tools/Localization.hand.txt for members with logic). Regenerate instead of editing by hand;
// LocalizationParityTests fails when this file and the Swift table drift apart.
// </auto-generated>
#nullable enable

namespace PokeTokenBar.Core;

/// <summary>
/// Port of <c>L</c> (Localization.swift): every UI string, per <see cref="AppLanguage"/>.
/// Pokémon names come separately from PokéAPI's multilingual data.
/// </summary>
public sealed partial class L
{
    public AppLanguage Lang { get; }

    public L(AppLanguage lang) => Lang = lang;

    /// <summary>
    /// Mirrors <c>RareCandy.xp</c> (CompanionModel.swift) for <see cref="ItemDescription"/>;
    /// LocalizationParityTests pins it to the Swift constant.
    /// </summary>
    internal const long RareCandyXp = 100_000_000;

    internal string T(string ko, string en, string ja, string es, string fr, string pt, string de, string ru) => Lang switch
    {
        AppLanguage.Ko => ko,
        AppLanguage.En => en,
        AppLanguage.Ja => ja,
        AppLanguage.Es => es,
        AppLanguage.Fr => fr,
        AppLanguage.Pt => pt,
        AppLanguage.De => de,
        AppLanguage.Ru => ru,
        _ => throw new ArgumentOutOfRangeException(nameof(Lang), Lang, null),
    };

"""


# ---------------------------------------------------------------- comments

HANGUL = re.compile(r'[가-힣]')


def flush_pending(pending, out, indent):
    """Emits an English comment run: `///` runs become a <summary>, `//` runs stay line comments."""
    if not pending:
        return
    if all(c.startswith('///') for c in pending):
        text = [c[3:].strip() for c in pending]
        if len(text) == 1:
            out.append('%s/// <summary>%s</summary>' % (indent, text[0]))
        else:
            out.append(indent + '/// <summary>')
            out.extend(indent + '/// ' + t for t in text)
            out.append(indent + '/// </summary>')
    else:
        out.extend(indent + c for c in pending)
    pending.clear()


def load_hand(path):
    text = open(path, encoding='utf-8').read()
    blocks = dict(re.findall(r'// PORT (\w+)\n(.*?)\n[ \t]*// END \1', text, re.S))
    docs = dict(re.findall(r'// DOC (\w+)\n(.*?)\n[ \t]*// END \1', text, re.S))
    marks = dict(re.findall(r'^MARK: (.*?) => (.*)$', text, re.M))
    return blocks, docs, marks


def main():
    src_path, out_path = sys.argv[1], sys.argv[2]
    hand, docs, marks = load_hand(sys.argv[3])
    src = open(src_path, encoding='utf-8').read()
    start, end = find_struct_body(src)
    body = src[start:end]
    indent = '    '
    out, simple, manual, missing, seen, pending, dropped = [], 0, [], [], set(), [], []
    for item in split_items(body):
        if item[0] == 'blank':
            if out and out[-1] != '':
                out.append('')
            continue
        if item[0] == 'comment':
            if item[1].startswith('// MARK:'):
                flush_pending(pending, out, indent)
                title = item[1][len('// MARK: '):]
                if HANGUL.search(title):
                    title = marks[title]  # KeyError: add the English title to the hand file
                out.append(indent + '// MARK: ' + title)
            else:
                pending.append(item[1])
            continue
        text = item[1]
        name = member_name(text)
        if name is None:  # let lang / init / t — written in the template
            continue
        if name == 't':
            continue
        key = name
        if key in seen:  # overloads such as label(for:)
            key = name + '_' + str(sum(1 for s in seen if s.split('_')[0] == name))
        seen.add(key)
        if key in docs:
            out.append(docs[key])
        elif pending and not any(HANGUL.search(c) for c in pending):
            flush_pending(pending, out, indent)
        elif pending:
            dropped.append(key)
        pending.clear()
        cs = None if key in hand else convert_simple(text, indent)
        if cs is not None:
            simple += 1
            out.append(cs)
        elif key in hand:
            manual.append(key)
            out.append(expand_tswift(hand[key], text, key))
        else:
            missing.append(key)
            out.append(indent + '#error PORT ' + key)
            out.extend(indent + '// ' + ln for ln in text.split('\n'))
    while out and out[-1] == '':
        out.pop()
    open(out_path, 'w', encoding='utf-8', newline='\n').write(HEADER + '\n'.join(out) + '\n}\n')
    print('simple=%d hand=%d missing=%d' % (simple, len(manual), len(missing)))
    for k in missing:
        print('MISSING', k)
    for k in dropped:
        print('KOREAN DOC DROPPED', k)
    unused = set(hand) - set(manual)
    for k in sorted(unused):
        print('UNUSED HAND BLOCK', k)


if __name__ == '__main__':
    main()
