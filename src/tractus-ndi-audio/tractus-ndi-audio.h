#ifndef TRACTUS_NDI_AUDIO_H
#define TRACTUS_NDI_AUDIO_H

#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif

#include <Processing.NDI.Lib.h>
#include <pipewire/filter.h>
#include <pipewire/pipewire.h>

#include <pthread.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <sys/un.h>

#define TRACTUS_NDI_SAMPLE_RATE 48000
#define TRACTUS_NDI_CHANNEL_COUNT 2U
#define TRACTUS_NDI_FRAME_SAMPLES 480U
#define TRACTUS_NDI_SEND_RING_CAPACITY 131072U
#define TRACTUS_NDI_SEND_RING_MASK (TRACTUS_NDI_SEND_RING_CAPACITY - 1U)
#define TRACTUS_NDI_SEND_PREBUFFER 2400U
#define TRACTUS_NDI_SEND_TARGET 2400U
#define TRACTUS_NDI_RECEIVE_RING_CAPACITY 131072U
#define TRACTUS_NDI_RECEIVE_RING_MASK (TRACTUS_NDI_RECEIVE_RING_CAPACITY - 1U)
#define TRACTUS_NDI_RECEIVE_TARGET_QUEUE 1440U
#define TRACTUS_NDI_RECEIVE_MAX_REQUEST 2048U
#define TRACTUS_NDI_MAX_SENDER_NAME 127U
#define TRACTUS_NDI_MAX_RECEIVER_NAME 511U
#define TRACTUS_NDI_MAX_DISCOVERED_SOURCES 32U
#define TRACTUS_NDI_CONTROL_BUFFER_SIZE 32768U

struct tractus_ndi_port {
    unsigned channel;
};

struct tractus_ndi_mono_ring {
    float samples[TRACTUS_NDI_SEND_RING_CAPACITY];
    _Atomic uint64_t write_position;
    _Atomic uint64_t read_position;
    _Atomic uint64_t underruns;
    _Atomic uint64_t overruns;
};

struct tractus_ndi_stereo_ring {
    float samples[TRACTUS_NDI_CHANNEL_COUNT][TRACTUS_NDI_RECEIVE_RING_CAPACITY];
    _Atomic uint64_t write_position;
    _Atomic uint64_t read_position;
    _Atomic uint64_t underruns;
    _Atomic uint64_t overruns;
};

struct tractus_ndi_configuration {
    uint64_t version;
    bool sender_enabled;
    bool receiver_enabled;
    char sender_name[TRACTUS_NDI_MAX_SENDER_NAME + 1U];
    char receiver_name[TRACTUS_NDI_MAX_RECEIVER_NAME + 1U];
};

struct tractus_ndi_status {
    uint64_t sequence;
    bool sender_enabled;
    bool sender_online;
    int sender_connections;
    float sender_peak_dbfs;
    float sender_rms_dbfs;
    float sender_queue_ms;
    uint64_t sender_underruns;
    uint64_t sender_overruns;
    bool receiver_enabled;
    bool receiver_connected;
    float receiver_peak_dbfs;
    float receiver_rms_dbfs;
    float receiver_queue_ms;
    uint64_t receiver_underruns;
    uint64_t receiver_overruns;
};

struct tractus_ndi_source_list {
    uint64_t sequence;
    unsigned count;
    char names[TRACTUS_NDI_MAX_DISCOVERED_SOURCES]
        [TRACTUS_NDI_MAX_RECEIVER_NAME + 1U];
};

struct tractus_ndi_data {
    struct pw_main_loop *loop;
    struct pw_filter *filter;
    struct tractus_ndi_port *sender_input;
    struct tractus_ndi_port *receiver_outputs[TRACTUS_NDI_CHANNEL_COUNT];
    struct tractus_ndi_mono_ring sender_ring;
    struct tractus_ndi_stereo_ring receiver_ring;
    _Atomic bool sender_active;
    _Atomic bool receiver_active;
    _Atomic bool running;

    pthread_mutex_t configuration_mutex;
    struct tractus_ndi_configuration configuration;
    pthread_mutex_t status_mutex;
    struct tractus_ndi_status status;
    pthread_mutex_t sources_mutex;
    struct tractus_ndi_source_list sources;

    const NDIlib_v6_3 *ndi;
    void *ndi_library;
    int control_fd;
    char socket_path[sizeof(((struct sockaddr_un *)0)->sun_path)];
    pthread_t sender_thread;
    pthread_t receiver_thread;
    pthread_t discovery_thread;
    pthread_t control_thread;
};

extern const struct pw_filter_events tractus_ndi_filter_events;

uint64_t tractus_ndi_mono_available(const struct tractus_ndi_mono_ring *ring);
void tractus_ndi_mono_clear(struct tractus_ndi_mono_ring *ring);
void tractus_ndi_mono_push(
    struct tractus_ndi_mono_ring *ring,
    const float *samples,
    uint32_t count);
uint32_t tractus_ndi_mono_pop(
    struct tractus_ndi_mono_ring *ring,
    float *destination,
    uint32_t count);
void tractus_ndi_mono_discard(struct tractus_ndi_mono_ring *ring, uint32_t count);

uint64_t tractus_ndi_stereo_available(const struct tractus_ndi_stereo_ring *ring);
void tractus_ndi_stereo_clear(struct tractus_ndi_stereo_ring *ring);
void tractus_ndi_stereo_push_frame(
    struct tractus_ndi_stereo_ring *ring,
    const NDIlib_audio_frame_v3_t *frame,
    float *peak,
    double *sum_squares,
    uint64_t *meter_samples);
void tractus_ndi_stereo_pop(
    struct tractus_ndi_stereo_ring *ring,
    float *destinations[TRACTUS_NDI_CHANNEL_COUNT],
    uint32_t count);

bool tractus_ndi_valid_name(const char *name, size_t maximum, bool allow_empty);
void *tractus_ndi_sender_thread_main(void *userdata);
void *tractus_ndi_receiver_thread_main(void *userdata);
void *tractus_ndi_discovery_thread_main(void *userdata);
int tractus_ndi_create_control_socket(struct tractus_ndi_data *data);
void *tractus_ndi_control_thread_main(void *userdata);

#endif
