#include "example.h"

#include <string.h>
#include <wchar.h>

int add(int a, int b)
{
    return a + b;
}

double multiply(double a, double b)
{
    return a * b;
}

char negate_char(char c)
{
    return (char)(-c);
}

signed char add_schar(signed char a, signed char b)
{
    return (signed char)(a + b);
}

unsigned char add_uchar(unsigned char a, unsigned char b)
{
    return (unsigned char)(a + b);
}

short add_short(short a, short b)
{
    return (short)(a + b);
}

unsigned short add_ushort(unsigned short a, unsigned short b)
{
    return (unsigned short)(a + b);
}

int add_int(int a, int b)
{
    return a + b;
}

unsigned int add_uint(unsigned int a, unsigned int b)
{
    return a + b;
}

long add_long(long a, long b)
{
    return a + b;
}

unsigned long add_ulong(unsigned long a, unsigned long b)
{
    return a + b;
}

long long add_ll(long long a, long long b)
{
    return a + b;
}

unsigned long long add_ull(unsigned long long a, unsigned long long b)
{
    return a + b;
}

float add_float(float a, float b)
{
    return a + b;
}

double add_double(double a, double b)
{
    return a + b;
}

void *identity_ptr(void *p)
{
    return p;
}

int increment(int *value)
{
    if (value == 0)
        return 0;
    return ++(*value);
}

uint64_t add_u64(uint64_t a, uint64_t b)
{
    return a + b;
}

Point make_point(int x, double y)
{
    Point p;
    p.x = x;
    p.y = y;
    return p;
}

void mutate_point(Point *point)
{
    if (point == 0)
        return;
    point->x += 1;
    point->y += 1.0;
}

double point_sum(Point point)
{
    return (double)point.x + point.y;
}

Point point_add(Point a, Point b)
{
    Point r;
    r.x = a.x + b.x;
    r.y = a.y + b.y;
    return r;
}

double nested_sum(NestedPoint n)
{
    return (double)n.x + (double)n.inner.x + n.inner.y;
}

int buffer_sum(Buffer *buffer)
{
    int s = 0;
    int i;
    for (i = 0; i < 4; i++)
        s += buffer->values[i];
    return s;
}

/* ---- callbacks ---- */

void invoke_callback(Callback callback, int value)
{
    if (callback != 0)
        callback(value);
}

static Callback g_callback = 0;

void set_callback(Callback callback)
{
    g_callback = callback;
}

void fire_callback(int value)
{
    if (g_callback != 0)
        g_callback(value);
}

void sort2(int a, int b, Comparator cmp, int *result)
{
    if (result == 0)
        return;
    if (cmp != 0 && cmp(&a, &b) > 0)
        result[0] = b;
    else
        result[0] = a;
}

void invoke_callback_ex(void (*callback)(int), int value)
{
    if (callback != 0)
        callback(value);
}

/* ---- strings ---- */

const char *get_name(void)
{
    return "Hello from C";
}

int cstrlen(const char *s)
{
    return s == 0 ? -1 : (int)strlen(s);
}

const char *echo(const char *s)
{
    return s;
}

void fill(unsigned char *buf, int len)
{
    int i;
    if (buf == 0)
        return;
    for (i = 0; i < len; i++)
        buf[i] = (unsigned char)(i & 0xff);
}

int checksum(unsigned char *buf, int len)
{
    int i, s = 0;
    if (buf == 0)
        return 0;
    for (i = 0; i < len; i++)
        s += buf[i];
    return s;
}

const wchar_t *get_wide_name(void)
{
    return L"Wide";
}

int wcslen_c(const wchar_t *s)
{
    return s == 0 ? -1 : (int)wcslen(s);
}

int __stdcall add_stdcall(int a, int b)
{
    return a + b;
}
