// Minimal zero-allocation JSON reader for the line-based protocol.
//
// ArduinoJson (used on the Teensy) isn't available here. The wire protocol is small and regular —
// a top-level object of scalars plus at most one level of nesting ("binding" object in set_binding;
// "axes"/"keys" arrays in the M4 inject_* commands) — so a purpose-built tokenizer is enough and
// avoids pulling in a parser. json_parse_object() walks the immediate members of an object into a
// flat json_kv[] (values point INTO the source line, no copies); nested objects/arrays come back as
// raw slices you re-parse with json_parse_object()/json_array_ints().
#pragma once
#include <stdbool.h>
#include <stddef.h>

typedef enum {
  JSON_NONE = 0,
  JSON_STR,
  JSON_NUM,
  JSON_BOOL,
  JSON_NULL,
  JSON_OBJ,
  JSON_ARR,
} json_type;

typedef struct {
  const char *key;
  size_t      keylen;
  const char *val;    // STR: inner text (between quotes, still escaped). OBJ/ARR: includes delimiters.
  size_t      vallen; // NUM/BOOL/NULL: the literal.
  json_type   type;
} json_kv;

// Parse the immediate members of the first '{...}' in [s, s+len) into out[0..max). Returns the
// member count (may exceed max — only the first max are stored), or -1 on malformed input.
int json_parse_object(const char *s, size_t len, json_kv *out, int max);

const json_kv *json_find(const json_kv *kv, int n, const char *key);

// Typed accessors. Return false / -1 when the key is missing or of the wrong type.
bool json_get_str(const json_kv *kv, int n, const char *key, char *out, size_t outlen);
bool json_get_int(const json_kv *kv, int n, const char *key, long *out);
int  json_get_bool(const json_kv *kv, int n, const char *key); // -1 missing/!bool, else 0/1

// Parse a "[ n, n, ... ]" slice of integers into out[0..max). Returns count or -1 on malformed.
int json_array_ints(const char *arr, size_t len, long *out, int max);
