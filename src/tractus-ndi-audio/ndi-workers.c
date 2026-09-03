#include "tractus-ndi-audio.h"

#include <errno.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static float linear_to_dbfs(float value)
{
    return value <= 0.000001f ? -120.0f : 20.0f * log10f(value);
}

static void sleep_milliseconds(long milliseconds)
{
    struct timespec interval = {
        .tv_sec = milliseconds / 1000L,
        .tv_nsec = (milliseconds % 1000L) * 1000000L,
    };
    while (nanosleep(&interval, &interval) < 0 && errno == EINTR) {
    }
}

static void copy_configuration(
    struct tractus_ndi_data *data,
    struct tractus_ndi_configuration *configuration)
{
    pthread_mutex_lock(&data->configuration_mutex);
    *configuration = data->configuration;
    pthread_mutex_unlock(&data->configuration_mutex);
}

static void update_sender_status(
    struct tractus_ndi_data *data,
    bool enabled,
    bool online,
    int connections,
    float peak_dbfs,
    float rms_dbfs)
{
    pthread_mutex_lock(&data->status_mutex);
    data->status.sequence++;
    data->status.sender_enabled = enabled;
    data->status.sender_online = online;
    data->status.sender_connections = connections;
    data->status.sender_peak_dbfs = peak_dbfs;
    data->status.sender_rms_dbfs = rms_dbfs;
    data->status.sender_queue_ms =
        (float)tractus_ndi_mono_available(&data->sender_ring) * 1000.0f /
        TRACTUS_NDI_SAMPLE_RATE;
    data->status.sender_underruns = atomic_load_explicit(
        &data->sender_ring.underruns, memory_order_relaxed);
    data->status.sender_overruns = atomic_load_explicit(
        &data->sender_ring.overruns, memory_order_relaxed);
    pthread_mutex_unlock(&data->status_mutex);
}

static void update_receiver_status(
    struct tractus_ndi_data *data,
    bool enabled,
    bool connected,
    float peak_dbfs,
    float rms_dbfs)
{
    pthread_mutex_lock(&data->status_mutex);
    data->status.sequence++;
    data->status.receiver_enabled = enabled;
    data->status.receiver_connected = connected;
    data->status.receiver_peak_dbfs = peak_dbfs;
    data->status.receiver_rms_dbfs = rms_dbfs;
    data->status.receiver_queue_ms =
        (float)tractus_ndi_stereo_available(&data->receiver_ring) * 1000.0f /
        TRACTUS_NDI_SAMPLE_RATE;
    data->status.receiver_underruns = atomic_load_explicit(
        &data->receiver_ring.underruns, memory_order_relaxed);
    data->status.receiver_overruns = atomic_load_explicit(
        &data->receiver_ring.overruns, memory_order_relaxed);
    pthread_mutex_unlock(&data->status_mutex);
}

static void on_process(void *userdata, struct spa_io_position *position)
{
    struct tractus_ndi_data *data = userdata;
    uint32_t sample_count = position->clock.duration;
    float *sender_input = pw_filter_get_dsp_buffer(data->sender_input, sample_count);
    if (sender_input != NULL &&
        atomic_load_explicit(&data->sender_active, memory_order_relaxed)) {
        tractus_ndi_mono_push(&data->sender_ring, sender_input, sample_count);
    } else {
        tractus_ndi_mono_clear(&data->sender_ring);
    }

    float *receiver_outputs[TRACTUS_NDI_CHANNEL_COUNT];
    for (unsigned channel = 0; channel < TRACTUS_NDI_CHANNEL_COUNT; channel++) {
        receiver_outputs[channel] = pw_filter_get_dsp_buffer(
            data->receiver_outputs[channel], sample_count);
        if (receiver_outputs[channel] == NULL)
            return;
    }
    if (atomic_load_explicit(&data->receiver_active, memory_order_relaxed)) {
        tractus_ndi_stereo_pop(&data->receiver_ring, receiver_outputs, sample_count);
    } else {
        for (unsigned channel = 0; channel < TRACTUS_NDI_CHANNEL_COUNT; channel++)
            memset(receiver_outputs[channel], 0, sample_count * sizeof(float));
        tractus_ndi_stereo_clear(&data->receiver_ring);
    }
}

const struct pw_filter_events tractus_ndi_filter_events = {
    PW_VERSION_FILTER_EVENTS,
    .process = on_process,
};

void *tractus_ndi_sender_thread_main(void *userdata)
{
    struct tractus_ndi_data *data = userdata;
    NDIlib_send_instance_t sender = NULL;
    uint64_t applied_version = UINT64_MAX;
    float samples[TRACTUS_NDI_FRAME_SAMPLES];
    bool prebuffered = false;

    while (atomic_load_explicit(&data->running, memory_order_relaxed)) {
        struct tractus_ndi_configuration configuration;
        copy_configuration(data, &configuration);
        if (configuration.version != applied_version) {
            atomic_store_explicit(&data->sender_active, false, memory_order_release);
            if (sender != NULL) {
                data->ndi->send_destroy(sender);
                sender = NULL;
            }
            tractus_ndi_mono_clear(&data->sender_ring);
            prebuffered = false;
            applied_version = configuration.version;
            if (configuration.sender_enabled) {
                NDIlib_send_create_t settings = {
                    .p_ndi_name = configuration.sender_name,
                    .p_groups = NULL,
                    .clock_video = false,
                    .clock_audio = true,
                };
                sender = data->ndi->send_create(&settings);
                if (sender == NULL) {
                    fprintf(stderr, "Could not create NDI sender '%s'\n",
                        configuration.sender_name);
                }
            }
            atomic_store_explicit(
                &data->sender_active,
                configuration.sender_enabled && sender != NULL,
                memory_order_release);
        }

        if (!configuration.sender_enabled || sender == NULL) {
            update_sender_status(
                data, configuration.sender_enabled, false, 0, -120.0f, -120.0f);
            sleep_milliseconds(50);
            continue;
        }
        uint64_t available = tractus_ndi_mono_available(&data->sender_ring);
        if (!prebuffered) {
            if (available < TRACTUS_NDI_SEND_PREBUFFER) {
                update_sender_status(data, true, true,
                    data->ndi->send_get_no_connections(sender, 0), -120.0f, -120.0f);
                sleep_milliseconds(2);
                continue;
            }
            prebuffered = true;
        }
        if (available > TRACTUS_NDI_SEND_TARGET + TRACTUS_NDI_FRAME_SAMPLES) {
            tractus_ndi_mono_discard(&data->sender_ring,
                (uint32_t)(available - TRACTUS_NDI_SEND_TARGET));
        }

        uint32_t read = tractus_ndi_mono_pop(
            &data->sender_ring, samples, TRACTUS_NDI_FRAME_SAMPLES);
        if (read < TRACTUS_NDI_FRAME_SAMPLES) {
            memset(samples + read, 0,
                (TRACTUS_NDI_FRAME_SAMPLES - read) * sizeof(float));
            atomic_fetch_add_explicit(&data->sender_ring.underruns,
                TRACTUS_NDI_FRAME_SAMPLES - read, memory_order_relaxed);
        }
        float peak = 0.0f;
        double sum_squares = 0.0;
        for (unsigned sample = 0; sample < TRACTUS_NDI_FRAME_SAMPLES; sample++) {
            float absolute = fabsf(samples[sample]);
            if (absolute > peak)
                peak = absolute;
            sum_squares += (double)samples[sample] * samples[sample];
        }
        NDIlib_audio_frame_v3_t frame = {
            .sample_rate = TRACTUS_NDI_SAMPLE_RATE,
            .no_channels = 1,
            .no_samples = TRACTUS_NDI_FRAME_SAMPLES,
            .timecode = NDIlib_send_timecode_synthesize,
            .FourCC = NDIlib_FourCC_audio_type_FLTP,
            .p_data = (uint8_t *)samples,
            .channel_stride_in_bytes =
                TRACTUS_NDI_FRAME_SAMPLES * (int)sizeof(float),
            .p_metadata = NULL,
            .timestamp = 0,
        };
        data->ndi->send_send_audio_v3(sender, &frame);
        update_sender_status(
            data,
            true,
            true,
            data->ndi->send_get_no_connections(sender, 0),
            linear_to_dbfs(peak),
            linear_to_dbfs((float)sqrt(
                sum_squares / TRACTUS_NDI_FRAME_SAMPLES)));
    }
    if (sender != NULL)
        data->ndi->send_destroy(sender);
    atomic_store_explicit(&data->sender_active, false, memory_order_release);
    return NULL;
}

void *tractus_ndi_receiver_thread_main(void *userdata)
{
    struct tractus_ndi_data *data = userdata;
    NDIlib_recv_instance_t receiver = NULL;
    NDIlib_framesync_instance_t framesync = NULL;
    uint64_t applied_version = UINT64_MAX;
    float meter_peak = 0.0f;
    double meter_sum_squares = 0.0;
    uint64_t meter_samples = 0;
    unsigned status_counter = 0;

    while (atomic_load_explicit(&data->running, memory_order_relaxed)) {
        struct tractus_ndi_configuration configuration;
        copy_configuration(data, &configuration);
        if (configuration.version != applied_version) {
            atomic_store_explicit(&data->receiver_active, false, memory_order_release);
            if (framesync != NULL) {
                data->ndi->framesync_destroy(framesync);
                framesync = NULL;
            }
            if (receiver != NULL) {
                data->ndi->recv_destroy(receiver);
                receiver = NULL;
            }
            tractus_ndi_stereo_clear(&data->receiver_ring);
            applied_version = configuration.version;
            if (configuration.receiver_enabled) {
                NDIlib_recv_create_v3_t settings = {
                    .source_to_connect_to = {
                        .p_ndi_name = configuration.receiver_name,
                        .p_url_address = NULL,
                    },
                    .color_format = NDIlib_recv_color_format_fastest,
                    .bandwidth = NDIlib_recv_bandwidth_audio_only,
                    .allow_video_fields = false,
                    .p_ndi_recv_name = "Tractus USB Audio NDI Return",
                };
                receiver = data->ndi->recv_create_v3(&settings);
                if (receiver != NULL)
                    framesync = data->ndi->framesync_create(receiver);
                if (receiver == NULL || framesync == NULL) {
                    fprintf(stderr, "Could not create NDI receiver for '%s'\n",
                        configuration.receiver_name);
                }
            }
            atomic_store_explicit(
                &data->receiver_active,
                configuration.receiver_enabled && framesync != NULL,
                memory_order_release);
        }

        if (!configuration.receiver_enabled || receiver == NULL || framesync == NULL) {
            update_receiver_status(
                data, configuration.receiver_enabled, false, -120.0f, -120.0f);
            sleep_milliseconds(50);
            continue;
        }

        uint64_t queued = tractus_ndi_stereo_available(&data->receiver_ring);
        if (queued < TRACTUS_NDI_RECEIVE_TARGET_QUEUE) {
            uint32_t requested =
                (uint32_t)(TRACTUS_NDI_RECEIVE_TARGET_QUEUE - queued);
            if (requested > TRACTUS_NDI_RECEIVE_MAX_REQUEST)
                requested = TRACTUS_NDI_RECEIVE_MAX_REQUEST;
            NDIlib_audio_frame_v3_t frame = { 0 };
            data->ndi->framesync_capture_audio_v2(framesync, &frame,
                TRACTUS_NDI_SAMPLE_RATE, TRACTUS_NDI_CHANNEL_COUNT, (int)requested);
            tractus_ndi_stereo_push_frame(&data->receiver_ring, &frame,
                &meter_peak, &meter_sum_squares, &meter_samples);
            data->ndi->framesync_free_audio_v2(framesync, &frame);
        } else {
            sleep_milliseconds(2);
        }

        status_counter++;
        if (status_counter >= 40U) {
            bool connected = data->ndi->recv_get_no_connections(receiver) > 0;
            float rms = meter_samples > 0
                ? (float)sqrt(meter_sum_squares / (double)meter_samples)
                : 0.0f;
            update_receiver_status(
                data, true, connected, linear_to_dbfs(meter_peak), linear_to_dbfs(rms));
            meter_peak = 0.0f;
            meter_sum_squares = 0.0;
            meter_samples = 0;
            status_counter = 0;
        }
    }
    atomic_store_explicit(&data->receiver_active, false, memory_order_release);
    if (framesync != NULL)
        data->ndi->framesync_destroy(framesync);
    if (receiver != NULL)
        data->ndi->recv_destroy(receiver);
    return NULL;
}

static int compare_source_names(const void *left, const void *right)
{
    return strcmp((const char *)left, (const char *)right);
}

bool tractus_ndi_valid_name(const char *name, size_t maximum, bool allow_empty)
{
    size_t length = strlen(name);
    if ((!allow_empty && length == 0) || length > maximum)
        return false;
    for (size_t index = 0; index < length; index++) {
        unsigned char character = (unsigned char)name[index];
        if (character < 0x20U || character == 0x7fU)
            return false;
    }
    return true;
}

static void update_source_list(
    struct tractus_ndi_data *data,
    const NDIlib_source_t *sources,
    uint32_t source_count)
{
    struct tractus_ndi_source_list next = { 0 };
    for (uint32_t source = 0;
         source < source_count && next.count < TRACTUS_NDI_MAX_DISCOVERED_SOURCES;
         source++) {
        const char *name = sources[source].p_ndi_name;
        if (name == NULL || !tractus_ndi_valid_name(
                name, TRACTUS_NDI_MAX_RECEIVER_NAME, false)) {
            continue;
        }
        snprintf(next.names[next.count], sizeof(next.names[next.count]), "%s", name);
        next.count++;
    }
    qsort(next.names, next.count, sizeof(next.names[0]), compare_source_names);

    pthread_mutex_lock(&data->sources_mutex);
    bool changed = next.count != data->sources.count;
    for (unsigned source = 0; !changed && source < next.count; source++)
        changed = strcmp(next.names[source], data->sources.names[source]) != 0;
    if (changed) {
        uint64_t sequence = data->sources.sequence + 1U;
        data->sources = next;
        data->sources.sequence = sequence;
    }
    pthread_mutex_unlock(&data->sources_mutex);
}

void *tractus_ndi_discovery_thread_main(void *userdata)
{
    struct tractus_ndi_data *data = userdata;
    NDIlib_find_create_t settings = {
        .show_local_sources = true,
        .p_groups = NULL,
        .p_extra_ips = NULL,
    };
    NDIlib_find_instance_t finder = data->ndi->find_create_v2(&settings);
    if (finder == NULL) {
        fprintf(stderr, "Could not create NDI source finder\n");
        return NULL;
    }
    while (atomic_load_explicit(&data->running, memory_order_relaxed)) {
        uint32_t source_count = 0;
        const NDIlib_source_t *sources = data->ndi->find_get_current_sources(
            finder, &source_count);
        update_source_list(data, sources, source_count);
        data->ndi->find_wait_for_sources(finder, 500);
    }
    data->ndi->find_destroy(finder);
    return NULL;
}
