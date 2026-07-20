// Minimal JSON reader. See json_min.h.
#include "json_min.h"

#include <string.h>

static const char *skip_ws(const char *p, const char *e) {
  while (p < e && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) p++;
  return p;
}

// p at opening '"'. Advances past the closing '"'. Sets the inner slice [*vs, *vl). Returns the
// pointer just past the closing quote, or NULL on an unterminated string.
static const char *scan_string(const char *p, const char *e, const char **vs, size_t *vl) {
  if (p >= e || *p != '"') return NULL;
  p++;
  const char *start = p;
  while (p < e) {
    if (*p == '\\') { p += 2; continue; } // skip the escaped char (incl. \")
    if (*p == '"') {
      *vs = start;
      *vl = (size_t)(p - start);
      return p + 1;
    }
    p++;
  }
  return NULL;
}

// p at '{' or '['. Advances past the matching close, honoring nested strings/containers. Sets the
// whole slice [*vs, *vl) including the delimiters. Returns the pointer past the close, or NULL.
static const char *scan_container(const char *p, const char *e, const char **vs, size_t *vl) {
  if (p >= e || (*p != '{' && *p != '[')) return NULL;
  const char *start = p;
  int         depth = 0;
  while (p < e) {
    char c = *p;
    if (c == '"') {
      const char *s2;
      size_t      l2;
      const char *q = scan_string(p, e, &s2, &l2);
      if (!q) return NULL;
      p = q;
      continue;
    }
    if (c == '{' || c == '[') {
      depth++;
    } else if (c == '}' || c == ']') {
      depth--;
      if (depth == 0) {
        *vs = start;
        *vl = (size_t)(p - start + 1);
        return p + 1;
      }
    }
    p++;
  }
  return NULL;
}

// p at the first char of a scalar (number / true / false / null). Advances to its end.
static const char *scan_scalar(const char *p, const char *e, const char **vs, size_t *vl,
                               json_type *t) {
  const char *start = p;
  if (p < e && (*p == 't' || *p == 'f')) {
    while (p < e && *p >= 'a' && *p <= 'z') p++;
    *t = JSON_BOOL;
  } else if (p < e && *p == 'n') {
    while (p < e && *p >= 'a' && *p <= 'z') p++;
    *t = JSON_NULL;
  } else {
    while (p < e &&
           (*p == '-' || *p == '+' || *p == '.' || *p == 'e' || *p == 'E' ||
            (*p >= '0' && *p <= '9')))
      p++;
    *t = JSON_NUM;
  }
  if (p == start) return NULL;
  *vs = start;
  *vl = (size_t)(p - start);
  return p;
}

int json_parse_object(const char *s, size_t len, json_kv *out, int max) {
  const char *p = s, *e = s + len;
  while (p < e && *p != '{') p++;
  if (p >= e) return -1;
  p++; // past '{'
  int n = 0;
  for (;;) {
    p = skip_ws(p, e);
    if (p >= e) return -1;
    if (*p == '}') return n;
    if (*p == ',') {
      p++;
      continue;
    }
    const char *ks;
    size_t      kl;
    const char *q = scan_string(p, e, &ks, &kl);
    if (!q) return -1;
    p = skip_ws(q, e);
    if (p >= e || *p != ':') return -1;
    p = skip_ws(p + 1, e);
    if (p >= e) return -1;

    const char *vs = NULL;
    size_t      vl = 0;
    json_type   t  = JSON_NONE;
    const char *after;
    if (*p == '"') {
      after = scan_string(p, e, &vs, &vl);
      t     = JSON_STR;
    } else if (*p == '{') {
      after = scan_container(p, e, &vs, &vl);
      t     = JSON_OBJ;
    } else if (*p == '[') {
      after = scan_container(p, e, &vs, &vl);
      t     = JSON_ARR;
    } else {
      after = scan_scalar(p, e, &vs, &vl, &t);
    }
    if (!after) return -1;
    if (n < max) {
      out[n].key    = ks;
      out[n].keylen = kl;
      out[n].val    = vs;
      out[n].vallen = vl;
      out[n].type   = t;
    }
    n++;
    p = after;
  }
}

const json_kv *json_find(const json_kv *kv, int n, const char *key) {
  size_t kl = strlen(key);
  for (int i = 0; i < n; i++)
    if (kv[i].keylen == kl && memcmp(kv[i].key, key, kl) == 0) return &kv[i];
  return NULL;
}

bool json_get_str(const json_kv *kv, int n, const char *key, char *out, size_t outlen) {
  const json_kv *k = json_find(kv, n, key);
  if (!k || k->type != JSON_STR || outlen == 0) return false;
  size_t o = 0;
  for (size_t i = 0; i < k->vallen && o < outlen - 1; i++) {
    char c = k->val[i];
    if (c == '\\' && i + 1 < k->vallen) {
      char nx = k->val[++i];
      switch (nx) {
        case 'n': c = '\n'; break;
        case 't': c = '\t'; break;
        case 'r': c = '\r'; break;
        case 'b': c = '\b'; break;
        case 'f': c = '\f'; break;
        case '"': c = '"'; break;
        case '\\': c = '\\'; break;
        case '/': c = '/'; break;
        case 'u': // \uXXXX — not needed by the protocol; collapse to '?'
          c = '?';
          i += (i + 4 <= k->vallen) ? 4 : (k->vallen - i);
          break;
        default: c = nx; break;
      }
    }
    out[o++] = c;
  }
  out[o] = '\0';
  return true;
}

bool json_get_int(const json_kv *kv, int n, const char *key, long *out) {
  const json_kv *k = json_find(kv, n, key);
  if (!k || k->type != JSON_NUM) return false;
  long v   = 0;
  int  neg = 0;
  bool any = false;
  size_t i = 0;
  if (i < k->vallen && (k->val[i] == '-' || k->val[i] == '+')) {
    neg = (k->val[i] == '-');
    i++;
  }
  for (; i < k->vallen && k->val[i] >= '0' && k->val[i] <= '9'; i++) {
    v = v * 10 + (k->val[i] - '0');
    any = true;
  }
  if (!any) return false;
  *out = neg ? -v : v;
  return true;
}

int json_get_bool(const json_kv *kv, int n, const char *key) {
  const json_kv *k = json_find(kv, n, key);
  if (!k || k->type != JSON_BOOL) return -1;
  return (k->vallen == 4 && memcmp(k->val, "true", 4) == 0) ? 1 : 0;
}

int json_array_ints(const char *arr, size_t len, long *out, int max) {
  const char *p = arr, *e = arr + len;
  while (p < e && *p != '[') p++;
  if (p >= e) return -1;
  p++; // past '['
  int n = 0;
  for (;;) {
    while (p < e && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n' || *p == ',')) p++;
    if (p >= e) return -1;
    if (*p == ']') return n;
    long v   = 0;
    int  neg = 0;
    bool any = false;
    if (*p == '-' || *p == '+') {
      neg = (*p == '-');
      p++;
    }
    while (p < e && *p >= '0' && *p <= '9') {
      v = v * 10 + (*p - '0');
      p++;
      any = true;
    }
    if (!any) return -1;
    if (n < max) out[n] = neg ? -v : v;
    n++;
  }
}
