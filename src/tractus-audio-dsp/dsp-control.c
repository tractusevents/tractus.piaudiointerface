#include "tractus-audio-dsp.h"

#include <errno.h>
#include <math.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <unistd.h>

static bool parameters_are_valid(
    float master_gain,
    int duck_enabled,
    int trigger_mask,
    float threshold_dbfs,
    float duck_depth_db,
    float attack_ms,
    float hold_ms,
    float release_ms,
    const int enabled[TRACTUS_DSP_DEVICE_COUNT],
    const float gain[TRACTUS_DSP_DEVICE_COUNT],
    const int solo[TRACTUS_DSP_DEVICE_COUNT],
    int sidetone_enabled,
    float sidetone_gain,
    int ndi_receiver_enabled,
    float ndi_receiver_gain)
{
    if (!isfinite(master_gain) || master_gain < 0.0f || master_gain > 1.5f ||
        (duck_enabled != 0 && duck_enabled != 1) ||
        trigger_mask < 1 || trigger_mask >= (1 << (TRACTUS_DSP_DEVICE_COUNT + 1U)) ||
        threshold_dbfs < -90.0f || threshold_dbfs > 0.0f ||
        duck_depth_db < 0.0f || duck_depth_db > 60.0f ||
        attack_ms < 1.0f || attack_ms > 2000.0f ||
        hold_ms < 0.0f || hold_ms > 5000.0f ||
        release_ms < 1.0f || release_ms > 10000.0f ||
        (sidetone_enabled != 0 && sidetone_enabled != 1) ||
        !isfinite(sidetone_gain) || sidetone_gain < 0.0f || sidetone_gain > 1.5f ||
        (ndi_receiver_enabled != 0 && ndi_receiver_enabled != 1) ||
        !isfinite(ndi_receiver_gain) || ndi_receiver_gain < 0.0f ||
            ndi_receiver_gain > 1.5f) {
        return false;
    }

    for (unsigned device = 0; device < TRACTUS_DSP_DEVICE_COUNT; device++) {
        if ((enabled[device] != 0 && enabled[device] != 1) ||
            !isfinite(gain[device]) || gain[device] < 0.0f || gain[device] > 1.5f ||
            (solo[device] != 0 && solo[device] != 1)) {
            return false;
        }
    }
    return true;
}

static bool apply_set_command(struct tractus_dsp_data *data, const char *command)
{
    float master_gain;
    int duck_enabled;
    int trigger_mask;
    float threshold_dbfs;
    float duck_depth_db;
    float attack_ms;
    float hold_ms;
    float release_ms;
    int enabled[TRACTUS_DSP_DEVICE_COUNT];
    float gain[TRACTUS_DSP_DEVICE_COUNT];
    int solo[TRACTUS_DSP_DEVICE_COUNT];
    int sidetone_enabled;
    float sidetone_gain;
    int ndi_receiver_enabled;
    float ndi_receiver_gain;

    int parsed = sscanf(command,
        "SET %f %d %d %f %f %f %f %f "
        "%d %f %d %d %f %d %d %f %d %d %f %d "
        "%d %f %d %f",
        &master_gain, &duck_enabled, &trigger_mask, &threshold_dbfs,
        &duck_depth_db, &attack_ms, &hold_ms, &release_ms,
        &enabled[0], &gain[0], &solo[0],
        &enabled[1], &gain[1], &solo[1],
        &enabled[2], &gain[2], &solo[2],
        &enabled[3], &gain[3], &solo[3],
        &sidetone_enabled, &sidetone_gain,
        &ndi_receiver_enabled, &ndi_receiver_gain);
    if (parsed != 24 || !parameters_are_valid(
            master_gain, duck_enabled, trigger_mask, threshold_dbfs,
            duck_depth_db, attack_ms, hold_ms, release_ms, enabled, gain, solo,
            sidetone_enabled, sidetone_gain, ndi_receiver_enabled,
            ndi_receiver_gain)) {
        return false;
    }

    atomic_store_explicit(
        &data->parameters.master_gain, master_gain, memory_order_relaxed);
    atomic_store_explicit(
        &data->parameters.duck_enabled, duck_enabled != 0, memory_order_relaxed);
    atomic_store_explicit(
        &data->parameters.trigger_mask, (unsigned)trigger_mask, memory_order_relaxed);
    atomic_store_explicit(
        &data->parameters.threshold_dbfs, threshold_dbfs, memory_order_relaxed);
    atomic_store_explicit(
        &data->parameters.duck_depth_db, duck_depth_db, memory_order_relaxed);
    atomic_store_explicit(
        &data->parameters.attack_ms, attack_ms, memory_order_relaxed);
    atomic_store_explicit(
        &data->parameters.hold_ms, hold_ms, memory_order_relaxed);
    atomic_store_explicit(
        &data->parameters.release_ms, release_ms, memory_order_relaxed);
    for (unsigned device = 0; device < TRACTUS_DSP_DEVICE_COUNT; device++) {
        atomic_store_explicit(
            &data->parameters.output_enabled[device],
            enabled[device] != 0,
            memory_order_relaxed);
        atomic_store_explicit(
            &data->parameters.output_gain[device], gain[device], memory_order_relaxed);
        atomic_store_explicit(
            &data->parameters.output_solo[device],
            solo[device] != 0,
            memory_order_relaxed);
    }
    atomic_store_explicit(
        &data->parameters.sidetone_enabled,
        sidetone_enabled != 0,
        memory_order_relaxed);
    atomic_store_explicit(
        &data->parameters.sidetone_gain, sidetone_gain, memory_order_relaxed);
    atomic_store_explicit(
        &data->parameters.ndi_receiver_enabled,
        ndi_receiver_enabled != 0,
        memory_order_relaxed);
    atomic_store_explicit(
        &data->parameters.ndi_receiver_gain,
        ndi_receiver_gain,
        memory_order_relaxed);
    return true;
}

static void send_meter_frame(
    struct tractus_dsp_data *data,
    const struct sockaddr_un *subscriber,
    socklen_t subscriber_length,
    uint32_t sequence)
{
    struct tractus_dsp_meter_frame frame =
        data->meter_ring[sequence % TRACTUS_DSP_METER_RING_SIZE];
    char message[TRACTUS_DSP_CONTROL_BUFFER_SIZE];
    int length = snprintf(message, sizeof(message),
        "METER %u %d %.3f %.3f %.3f %.3f %.3f %.3f %.3f %.3f %.3f "
        "%.3f %.3f %.3f %.3f %.3f %.3f",
        frame.sequence,
        frame.ducking_active ? 1 : 0,
        frame.duck_gain_reduction_db,
        frame.source_peak_dbfs[0], frame.source_rms_dbfs[0],
        frame.source_peak_dbfs[1], frame.source_rms_dbfs[1],
        frame.source_peak_dbfs[2], frame.source_rms_dbfs[2],
        frame.source_peak_dbfs[3], frame.source_rms_dbfs[3],
        frame.source_peak_dbfs[TRACTUS_DSP_SIDETONE_SOURCE],
        frame.source_rms_dbfs[TRACTUS_DSP_SIDETONE_SOURCE],
        frame.source_peak_dbfs[TRACTUS_DSP_NDI_RECEIVER_SOURCE],
        frame.source_rms_dbfs[TRACTUS_DSP_NDI_RECEIVER_SOURCE],
        frame.mix_peak_dbfs, frame.mix_rms_dbfs);
    if (length > 0 && (size_t)length < sizeof(message)) {
        (void)sendto(data->control_fd, message, (size_t)length, MSG_DONTWAIT,
            (const struct sockaddr *)subscriber, subscriber_length);
    }
}

void *tractus_dsp_control_thread_main(void *userdata)
{
    struct tractus_dsp_data *data = userdata;
    struct sockaddr_un subscriber = { 0 };
    socklen_t subscriber_length = 0;
    uint32_t last_meter_sequence = 0;
    struct pollfd descriptor = { .fd = data->control_fd, .events = POLLIN };

    while (atomic_load_explicit(&data->running, memory_order_relaxed)) {
        int poll_result = poll(&descriptor, 1, 50);
        if (poll_result > 0 && (descriptor.revents & POLLIN) != 0) {
            char command[TRACTUS_DSP_CONTROL_BUFFER_SIZE];
            struct sockaddr_un peer = { 0 };
            socklen_t peer_length = sizeof(peer);
            ssize_t length = recvfrom(data->control_fd, command, sizeof(command) - 1, 0,
                (struct sockaddr *)&peer, &peer_length);
            if (length > 0) {
                command[length] = '\0';
                const char *response;
                if (strncmp(command, "SUBSCRIBE", 9) == 0) {
                    subscriber = peer;
                    subscriber_length = peer_length;
                    response = "OK SUBSCRIBED";
                } else if (apply_set_command(data, command)) {
                    response = "OK CONFIGURED";
                } else {
                    response = "ERROR INVALID COMMAND";
                }
                if (peer_length > sizeof(sa_family_t)) {
                    (void)sendto(data->control_fd, response, strlen(response), MSG_DONTWAIT,
                        (const struct sockaddr *)&peer, peer_length);
                }
            }
        } else if (poll_result < 0 && errno != EINTR) {
            break;
        }

        uint32_t meter_sequence = atomic_load_explicit(
            &data->meter_write_sequence, memory_order_acquire);
        if (subscriber_length > 0 && meter_sequence != 0 &&
            meter_sequence != last_meter_sequence) {
            send_meter_frame(data, &subscriber, subscriber_length, meter_sequence);
            last_meter_sequence = meter_sequence;
        }
    }
    return NULL;
}

int tractus_dsp_create_control_socket(struct tractus_dsp_data *data)
{
    const char *configured_path = getenv("TRACTUS_DSP_SOCKET");
    const char *runtime_directory = getenv("XDG_RUNTIME_DIR");
    int path_length;
    if (configured_path != NULL && configured_path[0] != '\0') {
        path_length = snprintf(
            data->socket_path, sizeof(data->socket_path), "%s", configured_path);
    } else {
        if (runtime_directory == NULL || runtime_directory[0] == '\0') {
            fprintf(stderr, "XDG_RUNTIME_DIR or TRACTUS_DSP_SOCKET must be set\n");
            return -1;
        }
        path_length = snprintf(data->socket_path, sizeof(data->socket_path),
            "%s/tractus-audio-dsp.sock", runtime_directory);
    }
    if (path_length <= 0 || (size_t)path_length >= sizeof(data->socket_path)) {
        fprintf(stderr, "DSP control socket path is too long\n");
        return -1;
    }

    data->control_fd = socket(AF_UNIX, SOCK_DGRAM | SOCK_CLOEXEC, 0);
    if (data->control_fd < 0) {
        perror("socket");
        return -1;
    }
    struct sockaddr_un address = { .sun_family = AF_UNIX };
    memcpy(address.sun_path, data->socket_path, (size_t)path_length + 1);
    unlink(data->socket_path);
    if (bind(data->control_fd, (const struct sockaddr *)&address, sizeof(address)) < 0) {
        perror("bind");
        close(data->control_fd);
        data->control_fd = -1;
        return -1;
    }
    if (chmod(data->socket_path, 0600) < 0)
        perror("chmod");
    return 0;
}
