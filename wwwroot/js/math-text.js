/* ═══════════════════════════════════════════════════════════════════════════
   math-text.js – readable text for AI-written maths.

   AI replies and OCR transcriptions arrive as prose with LaTeX mixed in
   ("$y_{1,2} = \pm 2$", "\frac{1}{2}", "\left(x-2\right)^2"). This module shows
   them as readable text ("y₁,₂ = ± 2", "1/2", "(x − 2)²") and renders markdown
   prose safely, building DOM nodes only — never HTML strings — so untrusted
   model output cannot inject markup.

   Notation it cannot convert reliably (\int, \begin{cases}, \overline, unknown
   commands…) is NOT guessed at: the original expression is kept, in data-latex
   and in the returned string, and shown formatted through KaTeX when available.

   Public API (window.MathText):
     toReadable(latex)   -> { text, ok }          one expression, no delimiters
     inline(str)         -> { text, hasComplex }  plain string, delimiters removed
     renderInto(el, str)                          markdown + maths into el (replaces content)
     typeset(el)                                  KaTeX for the complex spans left in el
   ═══════════════════════════════════════════════════════════════════════════ */
(function (global) {
  'use strict';

  // ── Symbol tables ─────────────────────────────────────────────────────────
  var SYMBOLS = {
    pm: '±', mp: '∓', times: '×', div: '÷', cdot: '·', ast: '∗',
    le: '≤', leq: '≤', ge: '≥', geq: '≥', ne: '≠', neq: '≠', approx: '≈', equiv: '≡', sim: '∼',
    infty: '∞', pi: 'π', theta: 'θ', alpha: 'α', beta: 'β', gamma: 'γ', delta: 'δ', Delta: 'Δ',
    lambda: 'λ', mu: 'μ', sigma: 'σ', Sigma: 'Σ', omega: 'ω', Omega: 'Ω', phi: 'φ', epsilon: 'ε', rho: 'ρ', tau: 'τ',
    rightarrow: '→', to: '→', leftarrow: '←', Rightarrow: '⇒', Leftarrow: '⇐', Leftrightarrow: '⇔', leftrightarrow: '↔',
    circ: '°', degree: '°', angle: '∠', triangle: '△', parallel: '∥', perp: '⊥',
    'in': '∈', notin: '∉', subset: '⊂', subseteq: '⊆', cup: '∪', cap: '∩', forall: '∀', exists: '∃', emptyset: '∅',
    ldots: '…', dots: '…', cdots: '…',
    lbrack: '[', rbrack: ']', lbrace: '{', rbrace: '}', lparen: '(', rparen: ')', langle: '⟨', rangle: '⟩',
    vert: '|', lvert: '|', rvert: '|'
  };
  var SPACES = { quad: '  ', qquad: '    ', ',': ' ', ';': ' ', ':': ' ', ' ': ' ', '!': '' };
  var FUNCTIONS = { sin: 1, cos: 1, tan: 1, cot: 1, sec: 1, csc: 1, log: 1, ln: 1, exp: 1, min: 1, max: 1, lim: 1, arcsin: 1, arccos: 1, arctan: 1 };
  var TEXT_WRAPPERS = { text: 1, textbf: 1, textit: 1, mathrm: 1, mathbf: 1, mathit: 1, operatorname: 1, boldsymbol: 1, mathbb: 1 };
  var IGNORED = { left: 1, right: 1, displaystyle: 1, textstyle: 1, big: 1, Big: 1, bigg: 1, Bigg: 1 };

  var SUP = { '0': '⁰', '1': '¹', '2': '²', '3': '³', '4': '⁴', '5': '⁵', '6': '⁶', '7': '⁷', '8': '⁸', '9': '⁹',
              '+': '⁺', '−': '⁻', '-': '⁻', '=': '⁼', '(': '⁽', ')': '⁾', 'n': 'ⁿ', 'i': 'ⁱ' };
  var SUB = { '0': '₀', '1': '₁', '2': '₂', '3': '₃', '4': '₄', '5': '₅', '6': '₆', '7': '₇', '8': '₈', '9': '₉',
              '+': '₊', '−': '₋', '-': '₋', '=': '₌', '(': '₍', ')': '₎', ',': ',' };

  function has(obj, key) { return Object.prototype.hasOwnProperty.call(obj, key); }

  function mapAll(str, table) {
    var out = '';
    for (var i = 0; i < str.length; i++) {
      if (!has(table, str[i])) return null;
      out += table[str[i]];
    }
    return out;
  }

  // ── LaTeX → readable text ─────────────────────────────────────────────────
  // A small recursive-descent converter. `ok` turns false the moment something
  // is met that has no faithful plain-text form; the caller then keeps the original.
  function convert(src) {
    var i = 0, ok = true;

    function skipSpaces() { while (i < src.length && /\s/.test(src[i])) i++; }

    // One argument: a {group}, a \command, or a single character.
    function readArgSource() {
      skipSpaces();
      if (i >= src.length) return '';
      if (src[i] === '{') {
        var depth = 0, start = i + 1;
        for (; i < src.length; i++) {
          if (src[i] === '\\') { i++; continue; }
          if (src[i] === '{') depth++;
          else if (src[i] === '}') { depth--; if (depth === 0) { i++; return src.slice(start, i - 1); } }
        }
        ok = false;                       // unbalanced braces
        return src.slice(start);
      }
      if (src[i] === '\\') {
        var m = /^\\([A-Za-z]+|[\s\S])/.exec(src.slice(i));
        i += m[0].length;
        return m[0];
      }
      return src[i++];
    }

    function sub(text) {                  // convert a fragment with a fresh parser
      var r = convert(text);
      if (!r.ok) ok = false;
      return r.text;
    }

    function rawText(text) {              // \text{…}: content verbatim, spaces kept
      return text.replace(/\\([{}%$&#_])/g, '$1').replace(/\\ /g, ' ');
    }

    // "(x − 2)²" is already one unit; only an unbracketed compound needs wrapping.
    function isBracketed(s) {
      if (s[0] !== '(') return false;
      var depth = 0;
      for (var k = 0; k < s.length; k++) {
        if (s[k] === '(') depth++;
        else if (s[k] === ')') { depth--; if (depth === 0) return /^[⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻ⁿⁱ]*$/.test(s.slice(k + 1)); }
      }
      return false;
    }
    function needsParens(s) { return !isBracketed(s) && /[\s+−*/×·÷=,]/.test(s.replace(/^−/, '')); }
    function wrap(s) { return needsParens(s) ? '(' + s + ')' : s; }

    function script(kind) {               // after ^ or _
      var arg = sub(readArgSource());
      var mapped = mapAll(arg, kind === '^' ? SUP : SUB);
      if (mapped !== null && arg !== '') return mapped;
      return kind + (arg.length > 1 ? '(' + arg + ')' : arg);
    }

    var out = '';
    var BINARY_AFTER = /[^\s(\[{=+−,;<>±∓×·÷/|]$/;   // previous char that makes +/−/= binary
    var skipSpace = false;

    while (i < src.length) {
      var c = src[i];

      if (c === '\\') {
        var m = /^\\([A-Za-z]+|[\s\S])/.exec(src.slice(i));
        var name = m[1];
        i += m[0].length;
        skipSpace = false;

        if (name === 'pm' || name === 'mp') {            // "y = ±2" as a sign, "a ± b" as an operator
          var before = out.replace(/ +$/, '');
          out = (before !== '' && BINARY_AFTER.test(before)) ? before + ' ' + SYMBOLS[name] + ' ' : out + SYMBOLS[name];
          skipSpace = true;
          continue;
        }
        if (has(SPACES, name)) { out += SPACES[name]; continue; }
        if (has(SYMBOLS, name)) { out += SYMBOLS[name]; continue; }
        if (has(IGNORED, name)) {
          if (name === 'left' || name === 'right') {     // "\left." is an invisible delimiter
            skipSpaces();
            if (src[i] === '.') i++;
          }
          continue;
        }
        if (has(FUNCTIONS, name)) { out += name; continue; }
        if (has(TEXT_WRAPPERS, name)) { out += rawText(readArgSource()); continue; }
        if (name === 'frac' || name === 'dfrac' || name === 'tfrac') {
          var num = sub(readArgSource());
          var den = sub(readArgSource());
          out += wrap(num) + '/' + wrap(den);
          continue;
        }
        if (name === 'sqrt') {
          var index = '';
          skipSpaces();
          if (src[i] === '[') {
            var close = src.indexOf(']', i);
            if (close < 0) { ok = false; } else { index = src.slice(i + 1, close); i = close + 1; }
          }
          var radicand = sub(readArgSource());
          var root = index ? (mapAll(index, SUP) || ('(' + index + ')')) + '√' : '√';
          out += root + (/^[\w.²³]+$/.test(radicand) ? radicand : '(' + radicand + ')');
          continue;
        }
        if (/^[{}%$&#_]$/.test(name)) { out += name; continue; }          // \{ \} \% \$ …

        ok = false;                                                      // \int, \begin, \overline, unknown…
        out += '\\' + name;
        continue;
      }

      if (c === '{') { out += sub(readArgSource()); continue; }
      if (c === '}') { i++; continue; }
      if (c === '^' || c === '_') { i++; skipSpace = false; out += script(c); continue; }
      if (c === '~') { out += ' '; i++; continue; }

      if (/\s/.test(c)) {
        i++;
        if (!skipSpace && out !== '' && !/\s$/.test(out)) out += ' ';
        continue;
      }

      if (c === '+' || c === '-' || c === '−' || c === '=' || c === '<' || c === '>') {
        var op = c === '-' ? '−' : c;
        i++;
        var prev = out.replace(/ +$/, '');
        // A sign ("-2", "(-2", "= -2") hugs its number; anything after an operand is binary.
        var isBinary = c === '=' || c === '<' || c === '>' || (prev !== '' && BINARY_AFTER.test(prev));
        if (isBinary) { out = prev + ' ' + op + ' '; }
        else          { out += op; }
        skipSpace = true;
        continue;
      }

      skipSpace = false;
      out += c;
      i++;
    }

    return { text: out.replace(/^ +| +$/g, ''), ok: ok };
  }

  // ── Splitting prose from maths ────────────────────────────────────────────
  // $…$ is only maths when it looks like it: the opener is followed by a
  // non-space, the closer is preceded by one and not followed by a digit
  // ("costs $5 and $6" stays prose), and it sits in one paragraph and is short.
  // This is what stops a stray "$" from turning a whole sentence into maths.
  function findInlineClose(t, from) {
    if (from >= t.length || /\s/.test(t[from])) return -1;
    var limit = Math.min(t.length, from + 400);
    for (var j = from; j < limit; j++) {
      var ch = t[j];
      if (ch === '\\') { j++; continue; }
      if (ch === '\n' && t[j + 1] === '\n') return -1;
      if (ch === '$') {
        if (/\s/.test(t[j - 1]) || /\d/.test(t[j + 1] || '')) return -1;
        return j;
      }
    }
    return -1;
  }

  function segment(text) {
    var segs = [], buf = '', i = 0, n = text.length;
    function flush() { if (buf) { segs.push({ type: 'text', value: buf }); buf = ''; } }
    function math(value, display, raw) { flush(); segs.push({ type: 'math', value: value, display: display, raw: raw }); }

    while (i < n) {
      var c = text[i], next = text[i + 1], end;
      if (c === '\\' && next === '(' && (end = text.indexOf('\\)', i + 2)) > 0) {
        math(text.slice(i + 2, end), false, text.slice(i, end + 2)); i = end + 2; continue;
      }
      if (c === '\\' && next === '[' && (end = text.indexOf('\\]', i + 2)) > 0) {
        math(text.slice(i + 2, end), true, text.slice(i, end + 2)); i = end + 2; continue;
      }
      if (c === '\\' && next === '$') { buf += '$'; i += 2; continue; }
      if (c === '$' && next === '$' && (end = text.indexOf('$$', i + 2)) > i + 1) {
        math(text.slice(i + 2, end), true, text.slice(i, end + 2)); i = end + 2; continue;
      }
      if (c === '$' && (end = findInlineClose(text, i + 1)) > i) {
        math(text.slice(i + 1, end), false, text.slice(i, end + 1)); i = end + 1; continue;
      }
      buf += c; i++;
    }
    flush();
    return segs;
  }

  // Maths the model forgot to delimit ("\left(x-2\right)^2+3" in a sentence).
  var BARE = /[^\s$]*\\[A-Za-z]+[^\s$]*/g;
  function convertBare(prose) {
    return prose.replace(BARE, function (token) {
      var tail = /[.,;:!?]+$/.exec(token);
      var core = tail ? token.slice(0, token.length - tail[0].length) : token;
      var r = convert(core);
      return r.ok ? r.text + (tail ? tail[0] : '') : token;
    });
  }

  /** Plain-string form: delimiters removed, simple maths readable, complex maths left as written. */
  function inline(str) {
    var hasComplex = false;
    var text = segment(String(str == null ? '' : str)).map(function (s) {
      if (s.type === 'text') return convertBare(s.value);
      var r = convert(s.value);
      if (r.ok) return r.text;
      hasComplex = true;
      return s.raw;                      // keep the original, delimiters included
    }).join('');
    return { text: text, hasComplex: hasComplex };
  }

  // ── Markdown prose → DOM (no innerHTML) ──────────────────────────────────
  var PH_OPEN = '', PH_CLOSE = '';

  function el(tag, className) {
    var e = document.createElement(tag);
    if (className) e.className = className;
    return e;
  }

  function mathNode(seg) {
    var r = convert(seg.value);
    var span = el('span', r.ok ? 'math-text' : 'math-text math-complex');
    if (r.ok) {
      span.textContent = r.text;
    } else {
      span.setAttribute('data-latex', seg.value);
      span.setAttribute('data-display', seg.display ? '1' : '0');
      span.setAttribute('title', 'Complex notation, shown formatted');
      span.textContent = seg.raw;        // KaTeX replaces this; the original stays if it cannot
    }
    return span;
  }

  // **bold**, *italic*, `code` over text whose maths is already replaced by placeholders.
  function inlineNodes(text, mathSegs, parent) {
    var re = /\*\*(?=\S)([\s\S]+?)\*\*|\*(?=[^\s*])([^*\n]+?)\*|`([^`\n]+)`/g;
    var last = 0, m;
    var placeholder = new RegExp(PH_OPEN + '(\\d+)' + PH_CLOSE);
    function plain(s) {
      var parts = s.split(placeholder);
      for (var k = 0; k < parts.length; k++) {
        if (k % 2 === 0) { if (parts[k]) parent.appendChild(document.createTextNode(convertBare(parts[k]))); }
        else parent.appendChild(mathNode(mathSegs[parseInt(parts[k], 10)]));
      }
    }
    while ((m = re.exec(text)) !== null) {
      plain(text.slice(last, m.index));
      var node;
      if (m[1] !== undefined)      { node = el('strong'); inlineNodes(m[1], mathSegs, node); }
      else if (m[2] !== undefined) { node = el('em');     inlineNodes(m[2], mathSegs, node); }
      else                         { node = el('code');   node.textContent = m[3]; }
      parent.appendChild(node);
      last = m.index + m[0].length;
    }
    plain(text.slice(last));
  }

  function lineNodes(line, parent) {
    var mathSegs = [];
    var withPlaceholders = segment(line).map(function (s) {
      if (s.type === 'text') return s.value;
      mathSegs.push(s);
      return PH_OPEN + (mathSegs.length - 1) + PH_CLOSE;
    }).join('');
    inlineNodes(withPlaceholders, mathSegs, parent);
  }

  function renderInto(target, str) {
    while (target.firstChild) target.removeChild(target.firstChild);
    var text = String(str == null ? '' : str).replace(/\r\n?/g, '\n');
    // Display maths ($$…$$) may span lines; keep each block on one logical line.
    text = text.replace(/\$\$([\s\S]+?)\$\$/g, function (all, body) { return '$$' + body.replace(/\n+/g, ' ') + '$$'; });

    var para = null, list = null, listTag = '';
    function endList() { list = null; listTag = ''; }

    text.split('\n').forEach(function (line) {
      if (!line.trim()) { para = null; endList(); return; }

      var heading = /^\s{0,3}#{1,6}\s+(.*)$/.exec(line);
      var bullet  = /^\s*[-*•]\s+(.*)$/.exec(line);
      var number  = /^\s*(\d+)[.)]\s+(.*)$/.exec(line);

      if (heading) {
        para = null; endList();
        var h = el('p', 'math-heading'), strong = el('strong');
        lineNodes(heading[1], strong);
        h.appendChild(strong);
        target.appendChild(h);
      } else if (bullet || number) {
        para = null;
        var tag = bullet ? 'ul' : 'ol';
        if (!list || listTag !== tag) { list = el(tag); listTag = tag; target.appendChild(list); }
        var li = el('li');
        lineNodes(bullet ? bullet[1] : number[2], li);
        list.appendChild(li);
      } else {
        endList();
        if (!para) { para = el('p'); target.appendChild(para); }
        else para.appendChild(el('br'));
        lineNodes(line, para);
      }
    });
  }

  /** KaTeX for the spans convert() could not handle. A no-op when KaTeX is not loaded. */
  function typeset(root) {
    if (!root || !global.katex) return;
    root.querySelectorAll('.math-complex').forEach(function (node) {
      try {
        global.katex.render(node.getAttribute('data-latex') || '', node, {
          throwOnError: false, displayMode: node.getAttribute('data-display') === '1'
        });
      } catch (e) { /* keep the original source text */ }
    });
  }

  global.MathText = { toReadable: convert, inline: inline, renderInto: renderInto, typeset: typeset };
})(typeof window !== 'undefined' ? window : globalThis);
