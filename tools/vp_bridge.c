#define _GNU_SOURCE
#include <arpa/inet.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <netinet/in.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/xattr.h>
#include <time.h>
#include <unistd.h>

#define PLANE_W 960
#define PLANE_H 540
#define Y_BYTES (PLANE_W * PLANE_H)
#define C_BYTES (Y_BYTES / 2)
#define TZ_MAX_PIXELS (512 * 1024)
#define TZ_CAPTURE_STRUCT_SIZE 256
#define PARAM_BYTES 256
#define MAX_POINTS 255

#define BAR_THRESH 18
#define MIN_BAR_PX 12
#define BAR_HOLD_FRAMES 8

#define PREF_MODE_PATH "/opt/usr/home/owner/apps_rw/io.gh.darkxeno.TizenGlow/data/.pref/56f559fa5b89837412eb582d6b1734ff5f17bd29"
#define PREF_HDR_PATH  "/opt/usr/home/owner/apps_rw/io.gh.darkxeno.TizenGlow/data/.pref/b6760485373316095adc098b38e886b6b08eed39"

typedef int (*fn_secvideo_t)(int reqW, int reqH, void *param);

typedef struct {
    void *pYAddr;
    void *pCAddr;
    uint32_t out_8;
    uint32_t out_12;
    uint32_t width;
    uint32_t height;
    uint32_t out_24;
    uint32_t out_28;
    uint32_t cmd_id;
} tzcapture_param_t;

typedef int (*fn_tz_init_t)(void *tzc);
typedef int (*fn_tz_capture_t)(void *tzc, tzcapture_param_t *param);
typedef int (*fn_tz_finalize_t)(void *tzc);

typedef struct __attribute__((packed)) {
    char magic[4];
    uint8_t version;
    uint8_t count;
    uint8_t rgb[MAX_POINTS * 3];
} tgvp_packet_t;

typedef struct {
    double x;
    double y;
} norm_point_t;

typedef enum {
    PROFILE_DETAILED = 0,
    PROFILE_BALANCED_PRO = 1,
    PROFILE_CINEMA = 2
} bridge_profile_t;

typedef struct {
    const char *name;
    double inset;
    int patch_w;
    int patch_h;
    int stride;
    float ambient_mix;
    float sat_scale;
    float ema_alpha;
    float max_step;
    float deadband;
} profile_params_t;

static const profile_params_t g_profiles[3] = {
    /* PROFILE_DETAILED: Fast gaming / edge-accurate */
    {
        .name = "detailed",
        .inset = 0.04,
        .patch_w = 20,
        .patch_h = 14,
        .stride = 2,
        .ambient_mix = 0.0f,
        .sat_scale = 1.0f,
        .ema_alpha = 0.75f,
        .max_step = 255.0f,
        .deadband = 0.0f
    },
    /* PROFILE_BALANCED_PRO: Smooth intermediate mode (no flash/flicker, responsive mood) */
    {
        .name = "balanced_pro",
        .inset = 0.10,
        .patch_w = 80,
        .patch_h = 54,
        .stride = 8,
        .ambient_mix = 0.22f,
        .sat_scale = 1.05f,
        .ema_alpha = 0.14f,
        .max_step = 2.6f,
        .deadband = 0.5f
    },
    /* PROFILE_CINEMA: Ultra-calm ambient mode (deep interior zones, S-curve + slow slew cap) */
    {
        .name = "cinema",
        .inset = 0.18,
        .patch_w = 180,
        .patch_h = 110,
        .stride = 12,
        .ambient_mix = 0.45f,
        .sat_scale = 1.05f,
        .ema_alpha = 0.035f,
        .max_step = 0.65f,
        .deadband = 0.8f
    }
};

static volatile sig_atomic_t g_running = 1;

static void handle_signal(int sig) {
    (void)sig;
    g_running = 0;
}

static inline uint8_t clamp_u8(int v) {
    if (v < 0) return 0;
    if (v > 255) return 255;
    return (uint8_t)v;
}

/* ST.2084 PQ EOTF lookup table: maps 8-bit PQ code (0..255) to linear light (0..10000 nits) */
static void build_pq_nits_lut(float nits_lut[256]) {
    const double m1 = 2610.0 / 16384.0;
    const double m2 = (2523.0 / 4096.0) * 128.0;
    const double c1 = 3424.0 / 4096.0;
    const double c2 = (2413.0 / 4096.0) * 32.0;
    const double c3 = (2392.0 / 4096.0) * 32.0;

    nits_lut[0] = 0.0f;
    for (int i = 1; i < 256; i++) {
        double v = (double)i / 255.0;
        double vp = pow(v, 1.0 / m2);
        double num = vp - c1;
        if (num < 0.0) num = 0.0;
        double den = c2 - c3 * vp;
        if (den < 1e-6) den = 1e-6;
        double nits = 10000.0 * pow(num / den, 1.0 / m1);
        nits_lut[i] = (float)nits;
    }
}

static inline float tone_map_linear_nits(float nits) {
    if (nits <= 0.02f) return 0.0f;
    /* Normalize to 250-nit reference white with soft Reinhard roll-off */
    float x = nits / 250.0f;
    float mapped = x / (1.0f + x * 0.40f);
    if (mapped > 1.0f) mapped = 1.0f;
    /* Gamma 1/2.0 (hardware vsqrt.f32) pairs with WledOutput gamma 2.0 */
    return 255.0f * sqrtf(mapped);
}

static int build_perimeter_points(int top, int right, int bottom, int left,
                                  double inset, norm_point_t *pts, int max_pts) {
    int total = top + right + bottom + left;
    if (total <= 0 || total > max_pts) return -1;
    if (inset < 0.02) inset = 0.02;
    if (inset > 0.35) inset = 0.35;
    double span = 1.0 - 2.0 * inset;
    int idx = 0;

    /* Clockwise starting top-left */
    for (int i = 0; i < top; i++) {
        pts[idx].x = inset + span * ((i + 0.5) / (double)top);
        pts[idx].y = inset;
        idx++;
    }
    for (int i = 0; i < right; i++) {
        pts[idx].x = 1.0 - inset;
        pts[idx].y = inset + span * ((i + 0.5) / (double)right);
        idx++;
    }
    for (int i = 0; i < bottom; i++) {
        pts[idx].x = 1.0 - inset - span * ((i + 0.5) / (double)bottom);
        pts[idx].y = 1.0 - inset;
        idx++;
    }
    for (int i = 0; i < left; i++) {
        pts[idx].x = inset;
        pts[idx].y = 1.0 - inset - span * ((i + 0.5) / (double)left);
        idx++;
    }
    return idx;
}

/* Push targetFps=30 to TizenGlow WebSocket (127.0.0.1:8091) when mode button is clicked */
static void enforce_target_fps_30(void) {
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) return;

    struct timeval tv = {0, 150000}; /* 150ms timeout */
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(8091);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) == 0) {
        const char *hs =
            "GET / HTTP/1.1\r\n"
            "Host: 127.0.0.1:8091\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n";
        if (send(fd, hs, strlen(hs), 0) > 0) {
            char rx[512];
            if (recv(fd, rx, sizeof(rx), 0) > 0) {
                const char *msg = "{\"Event\":0,\"key\":\"targetFps\",\"value\":\"30\"}";
                size_t len = strlen(msg);
                uint8_t frame[128];
                frame[0] = 0x81;
                frame[1] = (uint8_t)(0x80 | len);
                /* zero mask key -> payload unmasked bytes */
                frame[2] = 0; frame[3] = 0; frame[4] = 0; frame[5] = 0;
                memcpy(frame + 6, msg, len);
                send(fd, frame, 6 + len, 0);
                usleep(20000);
            }
        }
    }
    close(fd);
}

static int read_pref_token(const char *path, struct timespec *last_mtime, char *tok_buf, size_t tok_cap) {
    struct stat st;
    if (stat(path, &st) != 0) return 0;
    if (st.st_mtim.tv_sec == last_mtime->tv_sec &&
        st.st_mtim.tv_nsec == last_mtime->tv_nsec) {
        return 0;
    }

    int fd = open(path, O_RDONLY);
    if (fd < 0) return 0;
    char buf[128];
    ssize_t n = read(fd, buf, sizeof(buf) - 1);
    close(fd);
    if (n <= 4) return 0;

    *last_mtime = st.st_mtim;

    ssize_t end = n - 1;
    while (end >= 0 && buf[end] == '\0') end--;
    if (end < 0) return 0;
    ssize_t start = end;
    while (start > 0 && buf[start - 1] >= 'a' && buf[start - 1] <= 'z') start--;

    size_t tok_len = (size_t)(end - start + 1);
    if (tok_len == 0 || tok_len >= tok_cap) return 0;
    memcpy(tok_buf, buf + start, tok_len);
    tok_buf[tok_len] = '\0';
    return 1;
}

static int check_tizenglow_mode(struct timespec *last_mtime, bridge_profile_t *out_prof, char *mode_buf, size_t mode_cap) {
    if (!read_pref_token(PREF_MODE_PATH, last_mtime, mode_buf, mode_cap)) return 0;

    if (strcmp(mode_buf, "cinema") == 0) {
        *out_prof = PROFILE_CINEMA;
    } else if (strcmp(mode_buf, "optimized") == 0 ||
               strcmp(mode_buf, "balanced") == 0 ||
               strcmp(mode_buf, "balancedpro") == 0) {
        *out_prof = PROFILE_BALANCED_PRO;
    } else {
        *out_prof = PROFILE_DETAILED;
    }
    return 1;
}

static int check_tizenglow_hdr(struct timespec *last_mtime, int *out_hdr) {
    char tok[32];
    if (!read_pref_token(PREF_HDR_PATH, last_mtime, tok, sizeof(tok))) return 0;
    if (strcmp(tok, "off") == 0) {
        *out_hdr = 0;
        return 1;
    } else if (strcmp(tok, "on") == 0) {
        *out_hdr = 1;
        return 1;
    }
    return 0;
}

static void detect_black_bars(const uint8_t *y_buf, int w, int h, int y_stride,
                              int *active_top, int *active_bottom,
                              int *cand_top, int *cand_bottom, int *hold_count) {
    int top = 0;
    int max_bar = h / 4;
    int col_step = w / 16;
    if (col_step < 1) col_step = 1;

    for (int row = 0; row < max_bar; row++) {
        const uint8_t *line = y_buf + row * y_stride;
        int bright = 0;
        for (int col = col_step / 2; col < w; col += col_step) {
            if (line[col] > BAR_THRESH) {
                bright = 1;
                break;
            }
        }
        if (bright) break;
        top = row + 1;
    }

    int bot = h;
    for (int row = h - 1; row >= h - max_bar; row--) {
        const uint8_t *line = y_buf + row * y_stride;
        int bright = 0;
        for (int col = col_step / 2; col < w; col += col_step) {
            if (line[col] > BAR_THRESH) {
                bright = 1;
                break;
            }
        }
        if (bright) break;
        bot = row;
    }

    if (top < MIN_BAR_PX) top = 0;
    if (h - bot < MIN_BAR_PX) bot = h;
    if (bot - top < h / 3) {
        top = 0;
        bot = h;
    }

    if (top == *cand_top && bot == *cand_bottom) {
        if (*hold_count < BAR_HOLD_FRAMES) {
            (*hold_count)++;
        }
        if (*hold_count >= BAR_HOLD_FRAMES) {
            *active_top = top;
            *active_bottom = bot;
        }
    } else {
        *cand_top = top;
        *cand_bottom = bot;
        *hold_count = 1;
    }
}

static inline void yuv_to_rgb(int y_avg, int u_avg, int v_avg,
                              int use_bt601, int use_hdr,
                              const float pq_nits_lut[256],
                              float sat_scale,
                              float *r_out, float *g_out, float *b_out) {
    int c_val = y_avg - 16;
    if (c_val < 0) c_val = 0;

    int r, g, b;
    if (use_hdr) {
        /* BT.2020 limited-range integer approximation */
        r = (298 * c_val + 430 * v_avg + 128) >> 8;
        g = (298 * c_val - 48 * u_avg - 167 * v_avg + 128) >> 8;
        b = (298 * c_val + 548 * u_avg + 128) >> 8;
    } else if (use_bt601) {
        r = (298 * c_val + 409 * v_avg + 128) >> 8;
        g = (298 * c_val - 100 * u_avg - 208 * v_avg + 128) >> 8;
        b = (298 * c_val + 516 * u_avg + 128) >> 8;
    } else {
        r = (298 * c_val + 459 * v_avg + 128) >> 8;
        g = (298 * c_val - 55 * u_avg - 136 * v_avg + 128) >> 8;
        b = (298 * c_val + 541 * u_avg + 128) >> 8;
    }

    uint8_t ru = clamp_u8(r);
    uint8_t gu = clamp_u8(g);
    uint8_t bu = clamp_u8(b);

    float rf, gf, bf;

    if (use_hdr) {
        /* 1. Decode each PQ primary R', G', B' to true linear nits */
        float rl = pq_nits_lut[ru];
        float gl = pq_nits_lut[gu];
        float bl = pq_nits_lut[bu];

        /* 2. Apply BT.2020 -> BT.709 primary matrix in LINEAR light (nits) */
        float r7 =  1.6605f * rl - 0.5876f * gl - 0.0728f * bl;
        float g7 = -0.1246f * rl + 1.1329f * gl - 0.0083f * bl;
        float b7 = -0.0182f * rl - 0.1006f * gl + 1.1187f * bl;
        if (r7 < 0.0f) r7 = 0.0f;
        if (g7 < 0.0f) g7 = 0.0f;
        if (b7 < 0.0f) b7 = 0.0f;

        /* 3. Tone-map each linear BT.709 channel to 0..255 */
        rf = tone_map_linear_nits(r7);
        gf = tone_map_linear_nits(g7);
        bf = tone_map_linear_nits(b7);
    } else {
        rf = (float)ru;
        gf = (float)gu;
        bf = (float)bu;
    }

    if (sat_scale != 1.0f) {
        float lum = 0.2126f * rf + 0.7152f * gf + 0.0722f * bf;
        rf = lum + (rf - lum) * sat_scale;
        gf = lum + (gf - lum) * sat_scale;
        bf = lum + (bf - lum) * sat_scale;
    }
    if (rf < 0.0f) rf = 0.0f; else if (rf > 255.0f) rf = 255.0f;
    if (gf < 0.0f) gf = 0.0f; else if (gf > 255.0f) gf = 255.0f;
    if (bf < 0.0f) bf = 0.0f; else if (bf > 255.0f) bf = 255.0f;

    *r_out = rf;
    *g_out = gf;
    *b_out = bf;
}

static void sample_points_and_filter(const uint8_t *y_buf, const uint8_t *c_buf,
                                     int w, int h, int y_stride, int c_stride,
                                     int active_top, int active_bottom,
                                     const profile_params_t *prof,
                                     int is_nv21, int use_bt601,
                                     int use_hdr, const float pq_nits_lut[256],
                                     const norm_point_t *pts, int count,
                                     float *s1_rgb, float *s2_rgb, float *out_f_rgb,
                                     int *filter_init, uint8_t *out_rgb) {
    int half_w = prof->patch_w / 2;
    int half_h = prof->patch_h / 2;
    int stride = prof->stride > 0 ? prof->stride : 1;
    int active_h = active_bottom - active_top;
    if (active_h < 16) {
        active_top = 0;
        active_bottom = h;
        active_h = h;
    }

    float raw_rgb[MAX_POINTS * 3];
    float global_r = 0.0f, global_g = 0.0f, global_b = 0.0f;

    for (int i = 0; i < count; i++) {
        int cx = (int)(pts[i].x * (double)(w - 1));
        int cy = active_top + (int)(pts[i].y * (double)(active_h - 1));

        int x0 = cx - half_w;
        int x1 = cx + half_w;
        int y0 = cy - half_h;
        int y1 = cy + half_h;
        if (x0 < 0) x0 = 0;
        if (y0 < active_top) y0 = active_top;
        if (x1 >= w) x1 = w - 1;
        if (y1 >= active_bottom) y1 = active_bottom - 1;

        int32_t sum_y = 0, sum_u = 0, sum_v = 0;
        int samples = 0;

        for (int py = y0; py <= y1; py += stride) {
            const uint8_t *y_row = y_buf + py * y_stride;
            const uint8_t *c_row = c_buf + (py >> 1) * c_stride;
            for (int px = x0; px <= x1; px += stride) {
                int uv_idx = (px & ~1);
                uint8_t yv = y_row[px];
                uint8_t u = is_nv21 ? c_row[uv_idx + 1] : c_row[uv_idx];
                uint8_t v = is_nv21 ? c_row[uv_idx] : c_row[uv_idx + 1];
                sum_y += yv;
                sum_u += u;
                sum_v += v;
                samples++;
            }
        }

        float rf = 0.0f, gf = 0.0f, bf = 0.0f;
        if (samples > 0) {
            int y_avg = sum_y / samples;
            int u_avg = (sum_u / samples) - 128;
            int v_avg = (sum_v / samples) - 128;
            yuv_to_rgb(y_avg, u_avg, v_avg, use_bt601, use_hdr, pq_nits_lut,
                       prof->sat_scale, &rf, &gf, &bf);
        }

        raw_rgb[i * 3 + 0] = rf;
        raw_rgb[i * 3 + 1] = gf;
        raw_rgb[i * 3 + 2] = bf;
        global_r += rf;
        global_g += gf;
        global_b += bf;
    }

    if (count > 0) {
        global_r /= (float)count;
        global_g /= (float)count;
        global_b /= (float)count;
    }

    float amb = prof->ambient_mix;
    float loc = 1.0f - amb;
    float alpha = prof->ema_alpha;
    float max_step = prof->max_step;
    float deadband = prof->deadband;

    for (int i = 0; i < count * 3; i += 3) {
        float tr = raw_rgb[i + 0] * loc + global_r * amb;
        float tg = raw_rgb[i + 1] * loc + global_g * amb;
        float tb = raw_rgb[i + 2] * loc + global_b * amb;
        float targets[3] = {tr, tg, tb};

        for (int c = 0; c < 3; c++) {
            int idx = i + c;
            float target = targets[c];
            if (!(*filter_init)) {
                s1_rgb[idx] = target;
                s2_rgb[idx] = target;
                out_f_rgb[idx] = target;
            } else {
                /* 2-stage S-curve low-pass filter + slew-rate limiter */
                s1_rgb[idx] += alpha * (target - s1_rgb[idx]);
                float want = s2_rgb[idx] + alpha * (s1_rgb[idx] - s2_rgb[idx]);
                float diff = want - out_f_rgb[idx];

                if (fabsf(diff) < deadband * 0.25f &&
                    fabsf(target - out_f_rgb[idx]) < deadband) {
                    diff = 0.0f;
                } else {
                    if (diff > max_step) diff = max_step;
                    if (diff < -max_step) diff = -max_step;
                }
                out_f_rgb[idx] += diff;
                s2_rgb[idx] = out_f_rgb[idx];
            }
            out_rgb[idx] = clamp_u8((int)lroundf(out_f_rgb[idx]));
        }
    }
    *filter_init = 1;
}

static void add_ns(struct timespec *ts, long ns) {
    ts->tv_nsec += ns;
    while (ts->tv_nsec >= 1000000000L) {
        ts->tv_nsec -= 1000000000L;
        ts->tv_sec += 1;
    }
}

static void print_usage(const char *prog) {
    printf("Usage: %s [options]\n", prog);
    printf("  --lib <path>       Path to libvideo-capture.so.0.1.0\n");
    printf("  --host <ip>        UDP target IP (default: 127.0.0.1)\n");
    printf("  --port <port>      UDP target port (default: 19446)\n");
    printf("  --fps <n>          Target FPS (default: 30)\n");
    printf("  --patch <n>        Sampling patch size NxN override\n");
    printf("  --format <fmt>     nv12 (default) or nv21\n");
    printf("  --matrix <m>       bt709 (default) or bt601\n");
    printf("  --hdr / --no-hdr   Enable/disable PQ HDR->SDR LUT (default: on)\n");
    printf("  --black-bars / --no-black-bars  Letterbox detection (default: on)\n");
    printf("  --top <n>          Top edge points (default: 7)\n");
    printf("  --right <n>        Right edge points (default: 5)\n");
    printf("  --bottom <n>       Bottom edge points (default: 7)\n");
    printf("  --left <n>         Left edge points (default: 5)\n");
    printf("  --daemon           Fork, setsid, and close all inherited FDs >= 3\n");
    printf("  --log <path>       Redirect stdout/stderr to file (used with --daemon)\n");
    printf("  --verbose          Print periodic FPS/capture stats\n");
    printf("  --help             Show this help\n");
}

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);

    if (getenv("TZCAPTURE_SPOOFED") == NULL) {
        setenv("TZCAPTURE_SPOOFED", "1", 1);
        char **new_argv = calloc((size_t)argc + 1, sizeof(char *));
        if (new_argv) {
            new_argv[0] = "/usr/bin/tzcapturesample";
            for (int i = 1; i < argc; i++) {
                new_argv[i] = argv[i];
            }
            execv("/proc/self/exe", new_argv);
            free(new_argv);
        }
    }

    const char *lib_path = "libvideo-capture.so.0.1.0";
    const char *host = "127.0.0.1";
    const char *log_path = "/run/vp_bridge.log";
    int port = 19446;
    int fps = 30;
    int patch = 7;
    int is_nv21 = 0;
    int use_bt601 = 0;
    int use_hdr = 1;
    int use_black_bars = 1;
    int top = 7, right = 5, bottom = 7, left = 5;
    int daemon_mode = 0;
    int verbose = 0;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--lib") == 0 && i + 1 < argc) {
            lib_path = argv[++i];
        } else if (strcmp(argv[i], "--host") == 0 && i + 1 < argc) {
            host = argv[++i];
        } else if (strcmp(argv[i], "--port") == 0 && i + 1 < argc) {
            port = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--fps") == 0 && i + 1 < argc) {
            fps = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--patch") == 0 && i + 1 < argc) {
            patch = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--format") == 0 && i + 1 < argc) {
            const char *fmt = argv[++i];
            if (strcmp(fmt, "nv21") == 0) is_nv21 = 1;
            else if (strcmp(fmt, "nv12") == 0) is_nv21 = 0;
            else {
                fprintf(stderr, "Invalid format: %s (expected nv12 or nv21)\n", fmt);
                return 1;
            }
        } else if (strcmp(argv[i], "--matrix") == 0 && i + 1 < argc) {
            const char *m = argv[++i];
            if (strcmp(m, "bt601") == 0) use_bt601 = 1;
            else if (strcmp(m, "bt709") == 0) use_bt601 = 0;
            else {
                fprintf(stderr, "Invalid matrix: %s (expected bt709 or bt601)\n", m);
                return 1;
            }
        } else if (strcmp(argv[i], "--hdr") == 0) {
            use_hdr = 1;
        } else if (strcmp(argv[i], "--no-hdr") == 0) {
            use_hdr = 0;
        } else if (strcmp(argv[i], "--black-bars") == 0) {
            use_black_bars = 1;
        } else if (strcmp(argv[i], "--no-black-bars") == 0) {
            use_black_bars = 0;
        } else if (strcmp(argv[i], "--top") == 0 && i + 1 < argc) {
            top = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--right") == 0 && i + 1 < argc) {
            right = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--bottom") == 0 && i + 1 < argc) {
            bottom = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--left") == 0 && i + 1 < argc) {
            left = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--daemon") == 0) {
            daemon_mode = 1;
        } else if (strcmp(argv[i], "--log") == 0 && i + 1 < argc) {
            log_path = argv[++i];
        } else if (strcmp(argv[i], "--verbose") == 0) {
            verbose = 1;
        } else if (strcmp(argv[i], "--help") == 0) {
            print_usage(argv[0]);
            return 0;
        } else {
            fprintf(stderr, "Unknown option: %s\n", argv[i]);
            print_usage(argv[0]);
            return 1;
        }
    }

    (void)patch;
    if (fps < 1) fps = 1;
    if (fps > 120) fps = 120;

    static norm_point_t points[MAX_POINTS];
    bridge_profile_t cur_prof = PROFILE_DETAILED;
    struct timespec pref_mtime = {0, 0};
    struct timespec hdr_mtime = {0, 0};
    char ui_mode[32] = "detailed";

    if (check_tizenglow_mode(&pref_mtime, &cur_prof, ui_mode, sizeof(ui_mode))) {
        /* Initial mode loaded from TizenGlow preferences */
    }
    if (check_tizenglow_hdr(&hdr_mtime, &use_hdr)) {
        /* Initial HDR setting loaded from TizenGlow preferences */
    }

    int point_count = build_perimeter_points(top, right, bottom, left,
                                             g_profiles[cur_prof].inset,
                                             points, MAX_POINTS);
    if (point_count <= 0) {
        fprintf(stderr, "Invalid perimeter point count (must be 1..255)\n");
        return 1;
    }

    if (daemon_mode) {
        pid_t pid = fork();
        if (pid < 0) {
            perror("fork");
            return 1;
        }
        if (pid > 0) {
            printf("[vp_bridge] Spawned daemon pid=%d\n", (int)pid);
            return 0;
        }
        setsid();
        for (int fd = 3; fd < 1024; fd++) {
            close(fd);
        }
        freopen("/dev/null", "r", stdin);
        if (log_path && log_path[0]) {
            freopen(log_path, "w", stdout);
            freopen(log_path, "a", stderr);
            setvbuf(stdout, NULL, _IONBF, 0);
            setvbuf(stderr, NULL, _IONBF, 0);
        }
    }

    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = handle_signal;
    sigaction(SIGINT, &sa, NULL);
    sigaction(SIGTERM, &sa, NULL);

    void *lib = dlopen(lib_path, RTLD_NOW);
    if (!lib) lib = dlopen(lib_path, RTLD_LAZY);
    if (!lib) {
        fprintf(stderr, "dlopen(%s) failed: %s\n", lib_path, dlerror());
        return 1;
    }

    fn_secvideo_t fn_capture = (fn_secvideo_t)dlsym(lib, "secvideo_api_capture_video_main");
    if (!fn_capture) {
        fprintf(stderr, "dlsym(secvideo_api_capture_video_main) failed: %s\n", dlerror());
        dlclose(lib);
        return 1;
    }

    void *tz_lib = dlopen("/usr/lib/libtzcapturec.so", RTLD_NOW);
    if (!tz_lib) tz_lib = dlopen("libtzcapturec.so", RTLD_NOW);
    fn_tz_init_t fn_tz_init = tz_lib ? (fn_tz_init_t)dlsym(tz_lib, "TZCAPTURE_Initialize") : NULL;
    fn_tz_capture_t fn_tz_capture = tz_lib ? (fn_tz_capture_t)dlsym(tz_lib, "TZCAPTURE_Capture_screen") : NULL;
    fn_tz_finalize_t fn_tz_finalize = tz_lib ? (fn_tz_finalize_t)dlsym(tz_lib, "TZCAPTURE_Finalize") : NULL;
    void *tzc = NULL;
    int tz_ready = 0;
    if (fn_tz_init && fn_tz_capture && fn_tz_finalize) {
        tzc = calloc(1, TZ_CAPTURE_STRUCT_SIZE);
        if (tzc) {
            int tz_init_rc = fn_tz_init(tzc);
            if (tz_init_rc == 0) {
                tz_ready = 1;
                printf("[vp_bridge] TrustZone DRM capture (libtzcapturec.so cmd=2) initialized.\n");
            } else {
                fprintf(stderr, "[vp_bridge] TZCAPTURE_Initialize returned %d\n", tz_init_rc);
                free(tzc);
                tzc = NULL;
            }
        }
    }

    int sock = socket(AF_INET, SOCK_DGRAM, 0);
    if (sock < 0) {
        perror("socket");
        if (tz_ready && fn_tz_finalize && tzc) fn_tz_finalize(tzc);
        if (tz_lib) dlclose(tz_lib);
        dlclose(lib);
        return 1;
    }

    (void)fsetxattr(sock, "security.SMACK64IPOUT", "@", 1, 0);

    struct sockaddr_in dst;
    memset(&dst, 0, sizeof(dst));
    dst.sin_family = AF_INET;
    dst.sin_port = htons((uint16_t)port);
    if (inet_pton(AF_INET, host, &dst.sin_addr) != 1) {
        fprintf(stderr, "Invalid host IP: %s\n", host);
        close(sock);
        if (tz_ready && fn_tz_finalize && tzc) fn_tz_finalize(tzc);
        if (tz_lib) dlclose(tz_lib);
        dlclose(lib);
        return 1;
    }

    static uint8_t buf_y[TZ_MAX_PIXELS] __attribute__((aligned(64)));
    static uint8_t buf_c[TZ_MAX_PIXELS] __attribute__((aligned(64)));
    static uint8_t param[PARAM_BYTES] __attribute__((aligned(64)));
    float pq_nits_lut[256];
    build_pq_nits_lut(pq_nits_lut);

    static float s1_rgb[MAX_POINTS * 3];
    static float s2_rgb[MAX_POINTS * 3];
    static float out_f_rgb[MAX_POINTS * 3];
    int filter_init = 0;

    tgvp_packet_t pkt;
    pkt.magic[0] = 'T';
    pkt.magic[1] = 'G';
    pkt.magic[2] = 'V';
    pkt.magic[3] = 'P';
    pkt.version = 1;
    pkt.count = (uint8_t)point_count;
    size_t pkt_len = 6 + (size_t)point_count * 3;

    int active_top = 0, active_bottom = PLANE_H;
    int cand_top = 0, cand_bottom = PLANE_H;
    int hold_count = 0;

    printf("[vp_bridge] target=%s:%d fps=%d points=%d (%d/%d/%d/%d) mode=%s profile=%s fmt=%s matrix=%s hdr=%d bars=%d tz=%d\n",
           host, port, fps, point_count, top, right, bottom, left,
           ui_mode, g_profiles[cur_prof].name,
           is_nv21 ? "nv21" : "nv12",
           use_bt601 ? "bt601" : "bt709",
           use_hdr, use_black_bars, tz_ready);

    long frame_ns = 1000000000L / fps;
    struct timespec next_ts, stat_ts;
    clock_gettime(CLOCK_MONOTONIC, &next_ts);
    stat_ts = next_ts;

    uint64_t frames_ok = 0, frames_err = 0, tick_count = 0;
    int last_err_rc = 0;
    int use_tz = 0;

    while (g_running) {
        /* Check TizenGlow UI mode & HDR file mtime once every 15 frames (~0.5s, cached stat in RAM) */
        if ((++tick_count % 15) == 0) {
            bridge_profile_t new_prof = cur_prof;
            if (check_tizenglow_mode(&pref_mtime, &new_prof, ui_mode, sizeof(ui_mode))) {
                cur_prof = new_prof;
                build_perimeter_points(top, right, bottom, left,
                                       g_profiles[cur_prof].inset,
                                       points, MAX_POINTS);
                enforce_target_fps_30();
                printf("[vp_bridge] UI mode changed -> mode=%s profile=%s (zone=%dx%d stride=%d inset=%.2f amb=%.2f alpha=%.3f slew=%.2f)\n",
                       ui_mode, g_profiles[cur_prof].name,
                       g_profiles[cur_prof].patch_w, g_profiles[cur_prof].patch_h,
                       g_profiles[cur_prof].stride, g_profiles[cur_prof].inset,
                       g_profiles[cur_prof].ambient_mix, g_profiles[cur_prof].ema_alpha,
                       g_profiles[cur_prof].max_step);
            }
            if (check_tizenglow_hdr(&hdr_mtime, &use_hdr)) {
                printf("[vp_bridge] UI HDR setting changed -> hdr=%d (%s)\n",
                       use_hdr, use_hdr ? "HDR PQ + BT.2020->BT.709" : "SDR BT.709");
            }
        }

        memset(param, 0, sizeof(param));
        *(uint32_t *)(param + 0x00) = Y_BYTES;
        *(uint32_t *)(param + 0x04) = C_BYTES;
        *(uintptr_t *)(param + 0x10) = (uintptr_t)buf_y;
        *(uintptr_t *)(param + 0x14) = (uintptr_t)buf_c;
        *(uint32_t *)(param + 0x1c) = 0;

        int w = PLANE_W, h = PLANE_H, y_stride = PLANE_W, c_stride = PLANE_W;
        int sec_rc = fn_capture(PLANE_W, PLANE_H, param);
        int rc = sec_rc;

        if (sec_rc == 0) {
            if (use_tz) {
                use_tz = 0;
                printf("[vp_bridge] Switched to secvideo capture\n");
            }
            int ret_w = *(int32_t *)(param + 0x08);
            int ret_h = *(int32_t *)(param + 0x0c);
            int y_pitch = *(int32_t *)(param + 0x2c);
            int c_pitch = *(int32_t *)(param + 0x30);

            w = (ret_w > 0 && ret_w <= PLANE_W) ? ret_w : PLANE_W;
            h = (ret_h > 0 && ret_h <= PLANE_H) ? ret_h : PLANE_H;
            y_stride = (y_pitch >= w && y_pitch <= PLANE_W * 2) ? y_pitch : w;
            c_stride = (c_pitch >= w && c_pitch <= PLANE_W * 2) ? c_pitch : w;
        } else if (sec_rc == -4 && tz_ready) {
            tzcapture_param_t tz_param;
            memset(&tz_param, 0, sizeof(tz_param));
            tz_param.pYAddr = buf_y;
            tz_param.pCAddr = buf_c;
            tz_param.width = PLANE_W;
            tz_param.height = PLANE_H;
            tz_param.cmd_id = 2;

            int tz_rc = fn_tz_capture(tzc, &tz_param);
            if (tz_rc == 0 && tz_param.width > 0 && tz_param.height > 0) {
                rc = 0;
                w = ((int)tz_param.width <= PLANE_W) ? (int)tz_param.width : PLANE_W;
                h = ((int)tz_param.height <= PLANE_H) ? (int)tz_param.height : PLANE_H;
                y_stride = w;
                c_stride = w;
                if (!use_tz) {
                    use_tz = 1;
                    printf("[vp_bridge] Switched to TrustZone DRM capture (sec_rc=%d, cmd=2, %dx%d)\n",
                           sec_rc, w, h);
                }
            } else {
                use_tz = 0;
            }
        }

        if (rc == 0) {
            if (use_black_bars) {
                detect_black_bars(buf_y, w, h, y_stride,
                                  &active_top, &active_bottom,
                                  &cand_top, &cand_bottom, &hold_count);
            }

            sample_points_and_filter(buf_y, buf_c, w, h, y_stride, c_stride,
                                     active_top, active_bottom,
                                     &g_profiles[cur_prof],
                                     is_nv21, use_bt601,
                                     use_tz ? 0 : use_hdr, pq_nits_lut,
                                     points, point_count,
                                     s1_rgb, s2_rgb, out_f_rgb,
                                     &filter_init, pkt.rgb);

            sendto(sock, &pkt, pkt_len, 0, (struct sockaddr *)&dst, sizeof(dst));
            frames_ok++;
        } else {
            frames_err++;
            last_err_rc = rc;
            struct timespec backoff = {0, 200000000L};
            clock_nanosleep(CLOCK_MONOTONIC, 0, &backoff, NULL);
            clock_gettime(CLOCK_MONOTONIC, &next_ts);
        }

        if (verbose) {
            struct timespec now;
            clock_gettime(CLOCK_MONOTONIC, &now);
            double elapsed = (double)(now.tv_sec - stat_ts.tv_sec) +
                             (double)(now.tv_nsec - stat_ts.tv_nsec) / 1e9;
            if (elapsed >= 2.0) {
                printf("[vp_bridge] %.1f FPS (ok=%llu err=%llu(%d) src=%s mode=%s prof=%s bars=%d..%d first_rgb=%u,%u,%u)\n",
                       (double)frames_ok / elapsed,
                       (unsigned long long)frames_ok,
                       (unsigned long long)frames_err,
                       last_err_rc,
                       use_tz ? "tz_drm" : "secvideo",
                       ui_mode, g_profiles[cur_prof].name,
                       active_top, active_bottom,
                       pkt.rgb[0], pkt.rgb[1], pkt.rgb[2]);
                frames_ok = 0;
                frames_err = 0;
                stat_ts = now;
            }
        }

        add_ns(&next_ts, frame_ns);
        int sleep_rc;
        do {
            sleep_rc = clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &next_ts, NULL);
        } while (sleep_rc == EINTR && g_running);
    }

    printf("[vp_bridge] Shutting down cleanly.\n");
    close(sock);
    if (tz_ready && fn_tz_finalize && tzc) {
        fn_tz_finalize(tzc);
    }
    if (tz_lib) dlclose(tz_lib);
    dlclose(lib);
    return 0;
}
