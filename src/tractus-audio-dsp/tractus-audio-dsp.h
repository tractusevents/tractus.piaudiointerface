#ifndef TRACTUS_AUDIO_DSP_H
#define TRACTUS_AUDIO_DSP_H

#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif

#include <pipewire/filter.h>
#include <pipewire/pipewire.h>

#include <pthread.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>
#include <sys/un.h>

#define TRACTUS_DSP_DEVICE_COUNT 4U
#define TRACTUS_DSP_CHANNEL_COUNT 2U
#define TRACTUS_DSP_MIX_SOURCE_COUNT 6U
#define TRACTUS_DSP_SIDETONE_SOURCE 4U
#define TRACTUS_DSP_NDI_RECEIVER_SOURCE 5U
#define TRACTUS_DSP_METER_RING_SIZE 64U
#define TRACTUS_DSP_CONTROL_BUFFER_SIZE 2048U
#define TRACTUS_DSP_DEFAULT_SAMPLE_RATE 48000.0f

struct tractus_dsp_port {
    unsigned device;
    unsigned channel;
};

struct tractus_dsp_parameters {
    _Atomic float master_gain;
    _Atomic bool duck_enabled;
    _Atomic unsigned trigger_mask;
    _Atomic float threshold_dbfs;
    _Atomic float duck_depth_db;
    _Atomic float attack_ms;
    _Atomic float hold_ms;
    _Atomic float release_ms;
    _Atomic bool output_enabled[TRACTUS_DSP_DEVICE_COUNT];
    _Atomic float output_gain[TRACTUS_DSP_DEVICE_COUNT];
    _Atomic bool output_solo[TRACTUS_DSP_DEVICE_COUNT];
    _Atomic bool sidetone_enabled;
    _Atomic float sidetone_gain;
    _Atomic bool ndi_receiver_enabled;
    _Atomic float ndi_receiver_gain;
};

struct tractus_dsp_meter_frame {
    uint32_t sequence;
    bool ducking_active;
    float duck_gain_reduction_db;
    float source_peak_dbfs[TRACTUS_DSP_MIX_SOURCE_COUNT];
    float source_rms_dbfs[TRACTUS_DSP_MIX_SOURCE_COUNT];
    float mix_peak_dbfs;
    float mix_rms_dbfs;
};

struct tractus_dsp_data {
    struct pw_main_loop *loop;
    struct pw_filter *filter;
    struct tractus_dsp_port
        *input_ports[TRACTUS_DSP_MIX_SOURCE_COUNT][TRACTUS_DSP_CHANNEL_COUNT];
    struct tractus_dsp_port *output_ports[TRACTUS_DSP_CHANNEL_COUNT];
    struct tractus_dsp_parameters parameters;
    _Atomic bool running;

    float source_gain_state[TRACTUS_DSP_MIX_SOURCE_COUNT];
    float master_gain_state;
    float duck_gain_state;
    float detector_power;
    bool duck_active;
    uint64_t hold_frames_remaining;
    float sample_rate;

    float meter_source_peak[TRACTUS_DSP_MIX_SOURCE_COUNT];
    double meter_source_sum_squares[TRACTUS_DSP_MIX_SOURCE_COUNT];
    float meter_mix_peak;
    double meter_mix_sum_squares;
    uint64_t meter_frames;
    struct tractus_dsp_meter_frame meter_ring[TRACTUS_DSP_METER_RING_SIZE];
    _Atomic uint32_t meter_write_sequence;

    char socket_path[sizeof(((struct sockaddr_un *)0)->sun_path)];
    int control_fd;
    pthread_t control_thread;
};

extern const struct pw_filter_events tractus_dsp_filter_events;

void tractus_dsp_parameters_initialize(struct tractus_dsp_parameters *parameters);
int tractus_dsp_create_control_socket(struct tractus_dsp_data *data);
void *tractus_dsp_control_thread_main(void *userdata);

#endif
