#define _GNU_SOURCE
#include <arpa/inet.h>
#include <dlfcn.h>
#include <errno.h>
#include <math.h>
#include <netinet/in.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define PLANE_W 960
#define PLANE_H 540
#define Y_BYTES (PLANE_W * PLANE_H)
#define C_BYTES (Y_BYTES / 2)
#define PARAM_BYTES 256
#define MAX_POINTS 255

#define BAR_THRESH 18
#define MIN_BAR_PX 12
#define BAR_HOLD_FRAMES 8

typedef int (*fn_secvideo_t)(int reqW, int reqH, void *param);

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

/* ST.2084 PQ EOTF -> 203-nit SDR tone-map -> sRGB gamma 2.2 LUT */
static void build_pq_lut(uint8_t lut[256]) {
    const double m1 = 2610.0 / 16384.0;
    const double m2 = (2523.0 / 4096.0) * 128.0;
    const double c1 = 3424.0 / 4096.0;
    const double c2 = (2413.0 / 4096.0) * 32.0;
    const double c3 = (2392.0 / 4096.0) * 32.0;
    const double ref_nits = 203.0;

    for (int i = 0; i < 256; i++) {
        double v = (double)i / 255.0;
        double vp = pow(v, 1.0 / m2);
        double num = vp - c1;
        if (num < 0.0) num = 0.0;
        double den = c2 - c3 * vp;
        if (den < 1e-6) den = 1e-6;
        double nits = 10000.0 * pow(num / den, 1.0 / m1);
        double lin = nits / ref_nits;
        /* Soft Reinhard roll-off above SDR white */
        double mapped = lin / (1.0 + lin * 0.35);
        if (mapped > 1.0) mapped = 1.0;
        double srgb = pow(mapped, 1.0 / 2.2);
        int out = (int)lround(srgb * 255.0);
        lut[i] = clamp_u8(out);
    }
}

static int build_perimeter_points(int top, int right, int bottom, int left,
                                  norm_point_t *pts, int max_pts) {
    int total = top + right + bottom + left;
    if (total <= 0 || total > max_pts) return -1;
    int idx = 0;

    /* Clockwise starting top-left */
    for (int i = 0; i < top; i++) {
        pts[idx].x = (i + 0.5) / (double)top;
        pts[idx].y = 0.03;
        idx++;
    }
    for (int i = 0; i < right; i++) {
        pts[idx].x = 0.97;
        pts[idx].y = (i + 0.5) / (double)right;
        idx++;
    }
    for (int i = 0; i < bottom; i++) {
        pts[idx].x = 1.0 - (i + 0.5) / (double)bottom;
        pts[idx].y = 0.97;
        idx++;
    }
    for (int i = 0; i < left; i++) {
        pts[idx].x = 0.03;
        pts[idx].y = 1.0 - (i + 0.5) / (double)left;
        idx++;
    }
    return idx;
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

static void sample_points_yuv(const uint8_t *y_buf, const uint8_t *c_buf,
                              int w, int h, int y_stride, int c_stride,
                              int active_top, int active_bottom,
                              int patch, int is_nv21, int use_bt601,
                              int use_hdr, const uint8_t pq_lut[256],
                              const norm_point_t *pts, int count,
                              uint8_t *out_rgb) {
    int half = patch / 2;
    int active_h = active_bottom - active_top;
    if (active_h < 16) {
        active_top = 0;
        active_bottom = h;
        active_h = h;
    }

    for (int i = 0; i < count; i++) {
        int cx = (int)(pts[i].x * (double)(w - 1));
        int cy = active_top + (int)(pts[i].y * (double)(active_h - 1));

        int x0 = cx - half;
        int x1 = cx + half;
        int y0 = cy - half;
        int y1 = cy + half;
        if (x0 < 0) x0 = 0;
        if (y0 < active_top) y0 = active_top;
        if (x1 >= w) x1 = w - 1;
        if (y1 >= active_bottom) y1 = active_bottom - 1;

        int32_t sum_y = 0, sum_u = 0, sum_v = 0;
        int samples = 0;

        for (int py = y0; py <= y1; py++) {
            const uint8_t *y_row = y_buf + py * y_stride;
            const uint8_t *c_row = c_buf + (py >> 1) * c_stride;
            for (int px = x0; px <= x1; px++) {
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

        if (samples == 0) {
            out_rgb[i * 3 + 0] = 0;
            out_rgb[i * 3 + 1] = 0;
            out_rgb[i * 3 + 2] = 0;
            continue;
        }

        int y_avg = sum_y / samples;
        int u_avg = (sum_u / samples) - 128;
        int v_avg = (sum_v / samples) - 128;

        int c_val = y_avg - 16;
        if (c_val < 0) c_val = 0;

        int r, g, b;
        if (use_bt601) {
            /* BT.601 limited-range integer approximation */
            r = (298 * c_val + 409 * v_avg + 128) >> 8;
            g = (298 * c_val - 100 * u_avg - 208 * v_avg + 128) >> 8;
            b = (298 * c_val + 516 * u_avg + 128) >> 8;
        } else {
            /* BT.709 limited-range integer approximation */
            r = (298 * c_val + 459 * v_avg + 128) >> 8;
            g = (298 * c_val - 55 * u_avg - 136 * v_avg + 128) >> 8;
            b = (298 * c_val + 541 * u_avg + 128) >> 8;
        }

        uint8_t ru = clamp_u8(r);
        uint8_t gu = clamp_u8(g);
        uint8_t bu = clamp_u8(b);

        if (use_hdr) {
            ru = pq_lut[ru];
            gu = pq_lut[gu];
            bu = pq_lut[bu];
        }

        out_rgb[i * 3 + 0] = ru;
        out_rgb[i * 3 + 1] = gu;
        out_rgb[i * 3 + 2] = bu;
    }
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
    printf("  --patch <n>        Sampling patch size NxN (default: 7)\n");
    printf("  --format <fmt>     nv12 (default) or nv21\n");
    printf("  --matrix <m>       bt709 (default) or bt601\n");
    printf("  --hdr / --no-hdr   Enable/disable PQ HDR->SDR LUT (default: on)\n");
    printf("  --black-bars / --no-black-bars  Letterbox detection (default: on)\n");
    printf("  --top <n>          Top edge points (default: 7)\n");
    printf("  --right <n>        Right edge points (default: 5)\n");
    printf("  --bottom <n>       Bottom edge points (default: 7)\n");
    printf("  --left <n>         Left edge points (default: 5)\n");
    printf("  --verbose          Print periodic FPS/capture stats\n");
    printf("  --help             Show this help\n");
}

int main(int argc, char **argv) {
    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);

    const char *lib_path = "libvideo-capture.so.0.1.0";
    const char *host = "127.0.0.1";
    int port = 19446;
    int fps = 30;
    int patch = 7;
    int is_nv21 = 0;
    int use_bt601 = 0;
    int use_hdr = 1;
    int use_black_bars = 1;
    int top = 7, right = 5, bottom = 7, left = 5;
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

    if (fps < 1) fps = 1;
    if (fps > 120) fps = 120;
    if (patch < 1) patch = 1;
    if (patch > 31) patch = 31;

    static norm_point_t points[MAX_POINTS];
    int point_count = build_perimeter_points(top, right, bottom, left, points, MAX_POINTS);
    if (point_count <= 0) {
        fprintf(stderr, "Invalid perimeter point count (must be 1..255)\n");
        return 1;
    }

    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = handle_signal;
    sigaction(SIGINT, &sa, NULL);
    sigaction(SIGTERM, &sa, NULL);

    void *lib = dlopen(lib_path, RTLD_NOW);
    if (!lib) lib = dlopen("/usr/lib/libvideo-capture.so.0.1.0", RTLD_NOW);
    if (!lib) {
        fprintf(stderr, "dlopen failed: %s\n", dlerror());
        return 1;
    }

    fn_secvideo_t fn_cap = (fn_secvideo_t)dlsym(lib, "secvideo_api_capture_video_main");
    if (!fn_cap) {
        fprintf(stderr, "dlsym(secvideo_api_capture_video_main) failed: %s\n", dlerror());
        dlclose(lib);
        return 1;
    }

    int sock = socket(AF_INET, SOCK_DGRAM, 0);
    if (sock < 0) {
        perror("socket");
        dlclose(lib);
        return 1;
    }

    struct sockaddr_in dst;
    memset(&dst, 0, sizeof(dst));
    dst.sin_family = AF_INET;
    dst.sin_port = htons((uint16_t)port);
    if (inet_pton(AF_INET, host, &dst.sin_addr) != 1) {
        fprintf(stderr, "Invalid target IP: %s\n", host);
        close(sock);
        dlclose(lib);
        return 1;
    }

    /* All runtime buffers allocated once before entering the capture loop */
    static uint8_t param[PARAM_BYTES];
    static uint8_t buf_y[Y_BYTES];
    static uint8_t buf_c[C_BYTES];
    static uint8_t pq_lut[256];
    static tgvp_packet_t pkt;

    build_pq_lut(pq_lut);
    memset(&pkt, 0, sizeof(pkt));
    memcpy(pkt.magic, "TGVP", 4);
    pkt.version = 1;
    pkt.count = (uint8_t)point_count;
    size_t pkt_len = 6 + (size_t)point_count * 3;

    int active_top = 0, active_bottom = PLANE_H;
    int cand_top = 0, cand_bottom = PLANE_H;
    int hold_count = 0;

    printf("[vp_bridge] target=%s:%d fps=%d points=%d (%d/%d/%d/%d) patch=%dx%d fmt=%s matrix=%s hdr=%d bars=%d\n",
           host, port, fps, point_count, top, right, bottom, left,
           patch, patch, is_nv21 ? "nv21" : "nv12",
           use_bt601 ? "bt601" : "bt709", use_hdr, use_black_bars);

    long frame_ns = 1000000000L / fps;
    struct timespec next_ts;
    clock_gettime(CLOCK_MONOTONIC, &next_ts);

    uint64_t frames_ok = 0;
    uint64_t frames_err = 0;
    struct timespec stat_ts = next_ts;

    while (g_running) {
        memset(param, 0, PARAM_BYTES);
        *(int32_t *)(param + 0x00) = (int32_t)Y_BYTES;
        *(int32_t *)(param + 0x04) = (int32_t)C_BYTES;
        *(uint32_t *)(param + 0x10) = (uint32_t)(uintptr_t)buf_y;
        *(uint32_t *)(param + 0x14) = (uint32_t)(uintptr_t)buf_c;
        *(int32_t *)(param + 0x1c) = 0;

        int rc = fn_cap(PLANE_W, PLANE_H, param);
        if (rc == 0) {
            int32_t rw = *(int32_t *)(param + 0x08);
            int32_t rh = *(int32_t *)(param + 0x0c);
            int32_t yp = *(int32_t *)(param + 0x2c);
            int32_t cp = *(int32_t *)(param + 0x30);

            int w = (rw >= 64 && rw <= PLANE_W) ? rw : PLANE_W;
            int h = (rh >= 64 && rh <= PLANE_H) ? rh : PLANE_H;
            int y_stride = (yp >= w && yp <= PLANE_W) ? yp : w;
            int c_stride = (cp >= w && cp <= PLANE_W) ? cp : w;

            if (use_black_bars) {
                detect_black_bars(buf_y, w, h, y_stride,
                                  &active_top, &active_bottom,
                                  &cand_top, &cand_bottom, &hold_count);
            }

            sample_points_yuv(buf_y, buf_c, w, h, y_stride, c_stride,
                              active_top, active_bottom, patch, is_nv21,
                              use_bt601, use_hdr, pq_lut, points,
                              point_count, pkt.rgb);

            sendto(sock, &pkt, pkt_len, 0, (struct sockaddr *)&dst, sizeof(dst));
            frames_ok++;
        } else {
            frames_err++;
            if (rc == -2) {
                fprintf(stderr, "[vp_bridge] Scaler wedged (rc=-2), exiting\n");
                break;
            }
            struct timespec backoff = {0, 200000000L};
            clock_nanosleep(CLOCK_MONOTONIC, 0, &backoff, NULL);
            clock_gettime(CLOCK_MONOTONIC, &next_ts);
            continue;
        }

        if (verbose) {
            struct timespec now;
            clock_gettime(CLOCK_MONOTONIC, &now);
            double elapsed = (double)(now.tv_sec - stat_ts.tv_sec) +
                             (double)(now.tv_nsec - stat_ts.tv_nsec) / 1e9;
            if (elapsed >= 2.0) {
                printf("[vp_bridge] %.1f FPS (ok=%llu err=%llu bars=%d..%d first_rgb=%u,%u,%u)\n",
                       (double)frames_ok / elapsed,
                       (unsigned long long)frames_ok,
                       (unsigned long long)frames_err,
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
    dlclose(lib);
    return 0;
}
