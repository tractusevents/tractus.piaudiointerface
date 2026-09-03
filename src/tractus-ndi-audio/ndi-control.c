#include "tractus-ndi-audio.h"

#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

static bool apply_command(
    struct tractus_ndi_data *data,
    char *command,
    char *error,
    size_t error_size)
{
    if (strncmp(command, "SET2 ", 5) == 0) {
        char *first_newline = strchr(command, '\n');
        if (first_newline == NULL) {
            snprintf(error, error_size, "ERROR SET2 requires sender and receiver names");
            return false;
        }
        *first_newline = '\0';
        char *second_newline = strchr(first_newline + 1, '\n');
        if (second_newline == NULL) {
            snprintf(error, error_size, "ERROR SET2 requires a receiver name");
            return false;
        }
        *second_newline = '\0';
        char *sender_name = first_newline + 1;
        char *receiver_name = second_newline + 1;
        char *trailing_newline = strchr(receiver_name, '\n');
        if (trailing_newline != NULL)
            *trailing_newline = '\0';
        int sender_enabled;
        int receiver_enabled;
        char extra;
        if (sscanf(command, "SET2 %d %d %c",
                &sender_enabled, &receiver_enabled, &extra) != 2 ||
            (sender_enabled != 0 && sender_enabled != 1) ||
            (receiver_enabled != 0 && receiver_enabled != 1) ||
            !tractus_ndi_valid_name(
                sender_name, TRACTUS_NDI_MAX_SENDER_NAME, false) ||
            !tractus_ndi_valid_name(receiver_name,
                TRACTUS_NDI_MAX_RECEIVER_NAME, receiver_enabled == 0)) {
            snprintf(error, error_size, "ERROR invalid SET2 configuration");
            return false;
        }
        pthread_mutex_lock(&data->configuration_mutex);
        data->configuration.version++;
        data->configuration.sender_enabled = sender_enabled != 0;
        data->configuration.receiver_enabled = receiver_enabled != 0;
        snprintf(data->configuration.sender_name,
            sizeof(data->configuration.sender_name), "%s", sender_name);
        snprintf(data->configuration.receiver_name,
            sizeof(data->configuration.receiver_name), "%s", receiver_name);
        pthread_mutex_unlock(&data->configuration_mutex);
        return true;
    }
    if (strncmp(command, "SET ", 4) == 0) {
        int enabled;
        int consumed = 0;
        if (sscanf(command, "SET %d %n", &enabled, &consumed) != 1 ||
            (enabled != 0 && enabled != 1) || consumed <= 0) {
            snprintf(error, error_size, "ERROR invalid SET command");
            return false;
        }
        char *name = command + consumed;
        if (!tractus_ndi_valid_name(name, TRACTUS_NDI_MAX_SENDER_NAME, false)) {
            snprintf(error, error_size, "ERROR invalid sender name");
            return false;
        }
        pthread_mutex_lock(&data->configuration_mutex);
        data->configuration.version++;
        data->configuration.sender_enabled = enabled != 0;
        snprintf(data->configuration.sender_name,
            sizeof(data->configuration.sender_name), "%s", name);
        pthread_mutex_unlock(&data->configuration_mutex);
        return true;
    }
    snprintf(error, error_size, "ERROR unknown command");
    return false;
}

static size_t format_status(
    struct tractus_ndi_data *data,
    char *buffer,
    size_t buffer_size,
    uint64_t *sequence)
{
    struct tractus_ndi_status status;
    pthread_mutex_lock(&data->status_mutex);
    status = data->status;
    pthread_mutex_unlock(&data->status_mutex);
    *sequence = status.sequence;
    int length = snprintf(buffer, buffer_size,
        "NDI %llu %d %d %d %.2f %.2f %.2f %llu %llu %d %d %.2f %.2f %.2f "
        "%llu %llu",
        (unsigned long long)status.sequence,
        status.sender_enabled ? 1 : 0,
        status.sender_online ? 1 : 0,
        status.sender_connections,
        status.sender_peak_dbfs,
        status.sender_rms_dbfs,
        status.sender_queue_ms,
        (unsigned long long)status.sender_underruns,
        (unsigned long long)status.sender_overruns,
        status.receiver_enabled ? 1 : 0,
        status.receiver_connected ? 1 : 0,
        status.receiver_peak_dbfs,
        status.receiver_rms_dbfs,
        status.receiver_queue_ms,
        (unsigned long long)status.receiver_underruns,
        (unsigned long long)status.receiver_overruns);
    return length > 0 && (size_t)length < buffer_size ? (size_t)length : 0;
}

static size_t format_sources(
    struct tractus_ndi_data *data,
    char *buffer,
    size_t buffer_size,
    uint64_t *sequence)
{
    struct tractus_ndi_source_list sources;
    pthread_mutex_lock(&data->sources_mutex);
    sources = data->sources;
    pthread_mutex_unlock(&data->sources_mutex);
    *sequence = sources.sequence;
    int length = snprintf(buffer, buffer_size, "SOURCES %llu",
        (unsigned long long)sources.sequence);
    if (length < 0 || (size_t)length >= buffer_size)
        return 0;
    size_t used = (size_t)length;
    for (unsigned source = 0; source < sources.count; source++) {
        length = snprintf(buffer + used, buffer_size - used,
            "\n%s", sources.names[source]);
        if (length < 0 || (size_t)length >= buffer_size - used)
            break;
        used += (size_t)length;
    }
    return used;
}

void *tractus_ndi_control_thread_main(void *userdata)
{
    struct tractus_ndi_data *data = userdata;
    struct sockaddr_un subscriber = { 0 };
    socklen_t subscriber_length = 0;
    bool subscribed = false;
    uint64_t last_status_sequence = UINT64_MAX;
    uint64_t last_sources_sequence = UINT64_MAX;
    char buffer[TRACTUS_NDI_CONTROL_BUFFER_SIZE];

    while (atomic_load_explicit(&data->running, memory_order_relaxed)) {
        struct pollfd poll_descriptor = {
            .fd = data->control_fd,
            .events = POLLIN,
        };
        int poll_result = poll(&poll_descriptor, 1, 100);
        if (poll_result > 0 && (poll_descriptor.revents & POLLIN) != 0) {
            struct sockaddr_un peer = { 0 };
            socklen_t peer_length = sizeof(peer);
            ssize_t length = recvfrom(data->control_fd, buffer, sizeof(buffer) - 1U, 0,
                (struct sockaddr *)&peer, &peer_length);
            if (length > 0) {
                buffer[length] = '\0';
                if (strcmp(buffer, "SUBSCRIBE") == 0) {
                    subscriber = peer;
                    subscriber_length = peer_length;
                    subscribed = true;
                    last_status_sequence = UINT64_MAX;
                    last_sources_sequence = UINT64_MAX;
                } else {
                    char error[128];
                    if (!apply_command(data, buffer, error, sizeof(error))) {
                        (void)sendto(data->control_fd, error, strlen(error), 0,
                            (struct sockaddr *)&peer, peer_length);
                    }
                }
            }
        }
        if (!subscribed)
            continue;

        uint64_t sequence;
        size_t length = format_status(data, buffer, sizeof(buffer), &sequence);
        if (sequence != last_status_sequence && length > 0) {
            if (sendto(data->control_fd, buffer, length, 0,
                    (struct sockaddr *)&subscriber, subscriber_length) < 0) {
                subscribed = false;
                continue;
            }
            last_status_sequence = sequence;
        }
        length = format_sources(data, buffer, sizeof(buffer), &sequence);
        if (sequence != last_sources_sequence && length > 0) {
            if (sendto(data->control_fd, buffer, length, 0,
                    (struct sockaddr *)&subscriber, subscriber_length) < 0) {
                subscribed = false;
                continue;
            }
            last_sources_sequence = sequence;
        }
    }
    return NULL;
}

int tractus_ndi_create_control_socket(struct tractus_ndi_data *data)
{
    const char *configured = getenv("TRACTUS_NDI_SOCKET");
    int length;
    if (configured != NULL && configured[0] != '\0') {
        length = snprintf(data->socket_path, sizeof(data->socket_path), "%s", configured);
    } else {
        const char *runtime = getenv("XDG_RUNTIME_DIR");
        if (runtime == NULL || runtime[0] == '\0')
            runtime = "/tmp";
        length = snprintf(data->socket_path, sizeof(data->socket_path),
            "%s/tractus-ndi-audio.sock", runtime);
    }
    if (length < 0 || (size_t)length >= sizeof(data->socket_path)) {
        fprintf(stderr, "NDI control socket path is too long\n");
        return -1;
    }

    unlink(data->socket_path);
    data->control_fd = socket(AF_UNIX, SOCK_DGRAM | SOCK_CLOEXEC, 0);
    if (data->control_fd < 0) {
        perror("socket");
        return -1;
    }
    struct sockaddr_un address = { .sun_family = AF_UNIX };
    snprintf(address.sun_path, sizeof(address.sun_path), "%s", data->socket_path);
    if (bind(data->control_fd, (struct sockaddr *)&address, sizeof(address)) < 0) {
        perror("bind");
        close(data->control_fd);
        data->control_fd = -1;
        return -1;
    }
    return 0;
}
