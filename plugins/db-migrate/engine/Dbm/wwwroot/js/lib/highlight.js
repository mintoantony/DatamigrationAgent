/* DBM.highlight — tiny T-SQL highlighter. Output is escaped HTML; token classes: tok-kw tok-str tok-num tok-com tok-id tok-fn tok-op.
   Classic script; also runs under Node (globalThis) for `node --test`. */
(function (DBM) {
  'use strict';

  function wordSet(text) {
    var set = Object.create(null);
    text.split(/\s+/).forEach(function (w) { if (w) set[w] = true; });
    return set;
  }

  var KEYWORDS = wordSet(
    'add all alter and any as asc authorization begin between break bulk by cascade case catch check checkpoint close ' +
    'clustered collate column commit constraint contains continue create cross current current_date current_time ' +
    'current_timestamp current_user cursor database dbcc deallocate declare default delete deny desc disable distinct ' +
    'drop else enable end escape except exec execute exists exit fetch file fillfactor for foreign from full function ' +
    'go goto grant group having holdlock identity identity_insert if in index inner insert intersect into is join key ' +
    'kill left like merge matched national nocheck nocount nolock nonclustered not null of off offset on open option ' +
    'or order outer output over percent pivot primary print proc procedure raiserror read references return revoke ' +
    'right rollback rowcount rows schema select set source statistics table tablock target then throw to top tran ' +
    'transaction trigger truncate try union unique unpivot update use using values view waitfor when where while with ' +
    'xact_abort ' +
    'bigint binary bit char date datetime datetime2 datetimeoffset decimal float image int max money nchar ntext ' +
    'numeric nvarchar real rowversion smalldatetime smallint smallmoney sql_variant text time timestamp tinyint ' +
    'uniqueidentifier varbinary varchar xml');

  var FUNCTIONS = wordSet(
    'abs avg cast ceiling charindex choose coalesce concat concat_ws convert count count_big cume_dist dateadd datediff ' +
    'datediff_big datefromparts datename datepart datetime2fromparts day dense_rank eomonth exp first_value floor ' +
    'format getdate getutcdate hashbytes iif isdate isjson isnull isnumeric json_value lag last_value lead left len ' +
    'log lower ltrim max min month newid newsequentialid ntile nullif object_id parse patindex power quotename rank ' +
    'replace replicate reverse right round row_number rtrim scope_identity sign space sqrt str string_agg ' +
    'string_split stuff substring sum switchoffset sysdatetime sysdatetimeoffset sysutcdatetime todatetimeoffset ' +
    'translate trim try_cast try_convert try_parse upper year');

  var WORD = /[A-Za-z_@#À-￿][A-Za-z0-9_@#$À-￿]*/y;
  var NUMBER = /0x[0-9A-Fa-f]*|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?/y;
  var OPERATOR = /<>|<=|>=|!=|!<|!>|\+=|-=|\*=|\/=|%=|&=|\|=|\^=|[=<>!+\-*\/%&|^~]/y;

  function isWordChar(ch) { return !!ch && /[A-Za-z0-9_@#$À-￿]/.test(ch); }

  function stickyMatch(re, src, i) {
    re.lastIndex = i;
    var m = re.exec(src);
    return m ? m[0] : null;
  }

  /* Closing-delimiter scan where a doubled closer is an escape ('' in strings, ]] in brackets, "" in quoted ids).
     Unterminated tokens run to the end of the text. */
  function scanQuoted(src, start, closer) {
    var j = start;
    while (j < src.length) {
      if (src[j] === closer) {
        if (src[j + 1] === closer) { j += 2; continue; }
        return j + 1;
      }
      j++;
    }
    return src.length;
  }

  /* -> [{c: 'kw'|'str'|'num'|'com'|'id'|'fn'|'op'|null, v: text}] ; concatenating v gives back the input. */
  function tokenize(text) {
    var src = String(text == null ? '' : text);
    var out = [];
    var i = 0;
    var n = src.length;
    function push(cls, v) {
      if (!v) return;
      var last = out[out.length - 1];
      if (cls === null && last && last.c === null) { last.v += v; return; }
      out.push({ c: cls, v: v });
    }
    while (i < n) {
      var ch = src[i];
      var nx = src[i + 1];
      var j;
      if (ch === '-' && nx === '-') {
        j = src.indexOf('\n', i);
        if (j < 0) j = n;
        push('com', src.slice(i, j)); i = j; continue;
      }
      if (ch === '/' && nx === '*') {
        var depth = 1;
        j = i + 2;
        while (j < n && depth > 0) {
          if (src[j] === '/' && src[j + 1] === '*') { depth++; j += 2; }
          else if (src[j] === '*' && src[j + 1] === '/') { depth--; j += 2; }
          else j++;
        }
        push('com', src.slice(i, j)); i = j; continue;
      }
      if (ch === "'" || ((ch === 'N' || ch === 'n') && nx === "'" && !isWordChar(src[i - 1]))) {
        j = scanQuoted(src, ch === "'" ? i + 1 : i + 2, "'");
        push('str', src.slice(i, j)); i = j; continue;
      }
      if (ch === '[') { j = scanQuoted(src, i + 1, ']'); push('id', src.slice(i, j)); i = j; continue; }
      if (ch === '"') { j = scanQuoted(src, i + 1, '"'); push('id', src.slice(i, j)); i = j; continue; }
      var word = stickyMatch(WORD, src, i);
      if (word) {
        var lower = word.toLowerCase();
        var cls = null;
        if (ch === '@' || ch === '#') cls = 'id';
        else {
          var k = i + word.length;
          while (k < n && (src[k] === ' ' || src[k] === '\t')) k++;
          if (src[k] === '(' && FUNCTIONS[lower]) cls = 'fn';
          else if (KEYWORDS[lower]) cls = 'kw';
        }
        push(cls, word); i += word.length; continue;
      }
      if (/[0-9]/.test(ch) || (ch === '.' && /[0-9]/.test(nx || ''))) {
        var num = stickyMatch(NUMBER, src, i);
        if (num) { push('num', num); i += num.length; continue; }
      }
      var op = stickyMatch(OPERATOR, src, i);
      if (op) { push('op', op); i += op.length; continue; }
      push(null, ch); i++;
    }
    return out;
  }

  function esc(s) {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  /* One escaped HTML string per source line; tokens that span lines are closed and reopened on each line.
     Lines split exactly like Dbm.Core.SqlGen.TaskListing: "\r\n" becomes "\n", a lone "\r" is NOT a break and stays in its
     line — the SQL view pairs these strings index-by-index with the listing's line numbers. */
  function sqlLines(text) {
    var src = String(text == null ? '' : text).replace(/\r\n/g, '\n');
    var lines = [''];
    tokenize(src).forEach(function (tok) {
      tok.v.split('\n').forEach(function (part, idx) {
        if (idx > 0) lines.push('');
        if (!part) return;
        lines[lines.length - 1] += tok.c ? '<span class="tok-' + tok.c + '">' + esc(part) + '</span>' : esc(part);
      });
    });
    return lines;
  }

  function sql(text) { return sqlLines(text).join('\n'); }

  DBM.highlight = { sql: sql, sqlLines: sqlLines, tokenize: tokenize };
})(globalThis.DBM = globalThis.DBM || {});
