#ifndef EXAMPLE_H
#define EXAMPLE_H

#include <stddef.h>
#include <stdint.h>

/* Phase 1 */
int add(int a, int b);
double multiply(double a, double b);

/* Phase 2 — signed/unsigned integer widths */
char negate_char(char c);
signed char add_schar(signed char a, signed char b);
unsigned char add_uchar(unsigned char a, unsigned char b);
short add_short(short a, short b);
unsigned short add_ushort(unsigned short a, unsigned short b);
int add_int(int a, int b);
unsigned int add_uint(unsigned int a, unsigned int b);
long add_long(long a, long b);
unsigned long add_ulong(unsigned long a, unsigned long b);
long long add_ll(long long a, long long b);
unsigned long long add_ull(unsigned long long a, unsigned long long b);

/* Phase 2 — floating point */
float add_float(float a, float b);
double add_double(double a, double b);

/* Phase 2 — pointers */
void *identity_ptr(void *p);
int increment(int *value);
uint64_t add_u64(uint64_t a, uint64_t b);

/* Phase 4 — structs */
typedef struct {
    int x;
    double y;
} Point;

Point make_point(int x, double y);
void mutate_point(Point *point);
double point_sum(Point point);
Point point_add(Point a, Point b);

typedef struct {
    int x;
    Point inner;
} NestedPoint;

double nested_sum(NestedPoint n);

typedef struct {
    int values[4];
    char name[8];
} Buffer;

int buffer_sum(Buffer *buffer);

/* Phase 5 — callbacks */
typedef void (*Callback)(int value);
typedef int (*Comparator)(const void *a, const void *b);

void invoke_callback(Callback callback, int value);

void set_callback(Callback callback);
void fire_callback(int value);

void sort2(int a, int b, Comparator cmp, int *result);

void invoke_callback_ex(void (*callback)(int), int value);

/* Phase 6 — strings and buffers */
const char *get_name(void);
int cstrlen(const char *s);
const char *echo(const char *s);
void fill(unsigned char *buf, int len);
int checksum(unsigned char *buf, int len);
const wchar_t *get_wide_name(void);
int wcslen_c(const wchar_t *s);

/* Phase 7 — calling conventions (stdcall; distinct on 32-bit x86) */
int __stdcall add_stdcall(int a, int b);

#endif /* EXAMPLE_H */
