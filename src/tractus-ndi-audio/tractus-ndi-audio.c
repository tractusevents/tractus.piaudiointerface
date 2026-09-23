#include "tractus-ndi-audio.h"

#include <pipewire/keys.h>
#include <spa/param/audio/raw.h>

#include <dlfcn.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static void initialize_data(struct tractus_ndi_data *data)
{
    data->control_fd = -1;
    atomic_init(&data->running, true);
    atomic_init(&data->sender_active, false);
    atomic_init(&data->sender_ring.write_position, 0);
    atomic_init(&data->sender_ring.read_position, 0);
    atomic_init(&data->sender_ring.underruns, 0);
    atomic_init(&data->sender_ring.overruns, 0);
    for (unsigned index = 0; index < TRACTUS_NDI_OUTPUT_SENDER_COUNT; index++) {
        atomic_init(&data->output_sender_active[index], false);
        atomic_init(&data->output_sender_rings[index].write_position, 0);
        atomic_init(&data->output_sender_rings[index].read_position, 0);
        atomic_init(&data->output_sender_rings[index].underruns, 0);
        atomic_init(&data->output_sender_rings[index].overruns, 0);
        data->output_sender_contexts[index] =
            (struct tractus_ndi_worker_context) { .data = data, .index = index };
        snprintf(data->configuration.output_sender_name[index],
            sizeof(data->configuration.output_sender_name[index]),
            "Audio Out (%u)", index + 1U);
        data->status.output_senders[index].peak_dbfs = -120.0f;
        data->status.output_senders[index].rms_dbfs = -120.0f;
    }
    for (unsigned index = 0; index < TRACTUS_NDI_RECEIVER_COUNT; index++) {
        atomic_init(&data->receiver_active[index], false);
        atomic_init(&data->receiver_rings[index].write_position, 0);
        atomic_init(&data->receiver_rings[index].read_position, 0);
        atomic_init(&data->receiver_rings[index].underruns, 0);
        atomic_init(&data->receiver_rings[index].overruns, 0);
        data->receiver_contexts[index] =
            (struct tractus_ndi_worker_context) { .data = data, .index = index };
        data->status.receivers[index].peak_dbfs = -120.0f;
        data->status.receivers[index].rms_dbfs = -120.0f;
    }
    pthread_mutex_init(&data->configuration_mutex, NULL);
    pthread_mutex_init(&data->status_mutex, NULL);
    pthread_mutex_init(&data->sources_mutex, NULL);

    data->configuration.version = 1;
    snprintf(data->configuration.sender_name,
        sizeof(data->configuration.sender_name), "Tractus USB Audio Microphone");
    data->status.sender.peak_dbfs = -120.0f;
    data->status.sender.rms_dbfs = -120.0f;
    data->sources.sequence = 1;
}

static int load_ndi(struct tractus_ndi_data *data)
{
    const char *library_path = getenv("TRACTUS_NDI_LIBRARY");
    if (library_path == NULL || library_path[0] == '\0')
        library_path = "libndi.so.6";
    data->ndi_library = dlopen(library_path, RTLD_LOCAL | RTLD_NOW);
    if (data->ndi_library == NULL) {
        fprintf(stderr, "Could not load NDI library: %s\n", dlerror());
        return -1;
    }
    const NDIlib_v6_3 *(*load)(void) = NULL;
    *(void **)(&load) = dlsym(data->ndi_library, "NDIlib_v6_3_load");
    if (load == NULL) {
        fprintf(stderr, "NDI library does not expose NDIlib_v6_3_load\n");
        dlclose(data->ndi_library);
        data->ndi_library = NULL;
        return -1;
    }
    data->ndi = load();
    if (data->ndi == NULL || !data->ndi->initialize()) {
        fprintf(stderr, "Could not initialize NDI\n");
        dlclose(data->ndi_library);
        data->ndi_library = NULL;
        return -1;
    }
    return 0;
}

static void do_quit(void *userdata, int signal_number)
{
    (void)signal_number;
    struct tractus_ndi_data *data = userdata;
    pw_main_loop_quit(data->loop);
}

static int add_filter_ports(struct tractus_ndi_data *data)
{
    data->sender_input = pw_filter_add_port(
        data->filter,
        PW_DIRECTION_INPUT,
        PW_FILTER_PORT_FLAG_MAP_BUFFERS,
        sizeof(struct tractus_ndi_port),
        pw_properties_new(
            PW_KEY_FORMAT_DSP, "32 bit float mono audio",
            PW_KEY_PORT_NAME, "input_MONO",
            SPA_KEY_AUDIO_CHANNEL, "MONO",
            NULL),
        NULL,
        0);
    if (data->sender_input == NULL) {
        fprintf(stderr, "Could not create NDI sender input port\n");
        return -1;
    }

    for (unsigned output = 0; output < TRACTUS_NDI_OUTPUT_SENDER_COUNT; output++) {
        for (unsigned channel = 0; channel < TRACTUS_NDI_CHANNEL_COUNT; channel++) {
            char name[40];
            snprintf(name, sizeof(name), "input_output_%u_%s",
                output + 1U, channel == 0 ? "FL" : "FR");
            data->output_sender_inputs[output][channel] = pw_filter_add_port(
                data->filter,
                PW_DIRECTION_INPUT,
                PW_FILTER_PORT_FLAG_MAP_BUFFERS,
                sizeof(struct tractus_ndi_port),
                pw_properties_new(
                    PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                    PW_KEY_PORT_NAME, name,
                    SPA_KEY_AUDIO_CHANNEL, channel == 0 ? "FL" : "FR",
                    NULL),
                NULL,
                0);
            if (data->output_sender_inputs[output][channel] == NULL) {
                fprintf(stderr, "Could not create NDI output sender port %s\n", name);
                return -1;
            }
        }
    }

    for (unsigned receiver = 0; receiver < TRACTUS_NDI_RECEIVER_COUNT; receiver++) {
        for (unsigned channel = 0; channel < TRACTUS_NDI_CHANNEL_COUNT; channel++) {
            char name[40];
            snprintf(name, sizeof(name), "output_receiver_%u_%s",
                receiver + 1U, channel == 0 ? "FL" : "FR");
            data->receiver_outputs[receiver][channel] = pw_filter_add_port(
                data->filter,
                PW_DIRECTION_OUTPUT,
                PW_FILTER_PORT_FLAG_MAP_BUFFERS,
                sizeof(struct tractus_ndi_port),
                pw_properties_new(
                    PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                    PW_KEY_PORT_NAME, name,
                    SPA_KEY_AUDIO_CHANNEL, channel == 0 ? "FL" : "FR",
                    NULL),
                NULL,
                0);
            if (data->receiver_outputs[receiver][channel] == NULL) {
                fprintf(stderr, "Could not create NDI receiver output port %s\n", name);
                return -1;
            }
        }
    }
    return 0;
}

int main(int argc, char *argv[])
{
    /* The receiver/sender rings are intentionally large and must not live on
     * the default 8 MiB process stack. */
    static struct tractus_ndi_data data;
    initialize_data(&data);

    if (load_ndi(&data) < 0)
        return 1;
    pw_init(&argc, &argv);
    data.loop = pw_main_loop_new(NULL);
    if (data.loop == NULL) {
        fprintf(stderr, "Could not create PipeWire main loop\n");
        return 1;
    }
    pw_loop_add_signal(pw_main_loop_get_loop(data.loop), SIGINT, do_quit, &data);
    pw_loop_add_signal(pw_main_loop_get_loop(data.loop), SIGTERM, do_quit, &data);

    data.filter = pw_filter_new_simple(
        pw_main_loop_get_loop(data.loop),
        "tractus_ndi_audio",
        pw_properties_new(
            PW_KEY_NODE_NAME, "tractus_ndi_audio",
            PW_KEY_NODE_DESCRIPTION, "Tractus NDI Audio",
            PW_KEY_MEDIA_TYPE, "Audio",
            PW_KEY_MEDIA_CATEGORY, "Duplex",
            PW_KEY_MEDIA_ROLE, "DSP",
            NULL),
        &tractus_ndi_filter_events,
        &data);
    if (data.filter == NULL) {
        fprintf(stderr, "Could not create PipeWire NDI filter\n");
        return 1;
    }
    if (add_filter_ports(&data) < 0)
        return 1;
    if (tractus_ndi_create_control_socket(&data) < 0)
        return 1;
    bool worker_failed = pthread_create(
            &data.sender_thread, NULL, tractus_ndi_sender_thread_main, &data) != 0;
    for (unsigned index = 0; !worker_failed && index < TRACTUS_NDI_OUTPUT_SENDER_COUNT; index++) {
        worker_failed = pthread_create(&data.output_sender_threads[index], NULL,
            tractus_ndi_output_sender_thread_main,
            &data.output_sender_contexts[index]) != 0;
    }
    for (unsigned index = 0; !worker_failed && index < TRACTUS_NDI_RECEIVER_COUNT; index++) {
        worker_failed = pthread_create(&data.receiver_threads[index], NULL,
            tractus_ndi_receiver_thread_main, &data.receiver_contexts[index]) != 0;
    }
    if (worker_failed || pthread_create(
            &data.discovery_thread, NULL, tractus_ndi_discovery_thread_main, &data) != 0 ||
        pthread_create(
            &data.control_thread, NULL, tractus_ndi_control_thread_main, &data) != 0) {
        fprintf(stderr, "Could not create NDI worker threads\n");
        return 1;
    }
    if (pw_filter_connect(data.filter, PW_FILTER_FLAG_RT_PROCESS, NULL, 0) < 0) {
        fprintf(stderr, "Could not connect PipeWire NDI filter\n");
        return 1;
    }

    fprintf(stderr, "Tractus NDI audio sender/receiver ready; control socket %s\n",
        data.socket_path);
    pw_main_loop_run(data.loop);

    atomic_store_explicit(&data.running, false, memory_order_release);
    pthread_join(data.sender_thread, NULL);
    for (unsigned index = 0; index < TRACTUS_NDI_OUTPUT_SENDER_COUNT; index++)
        pthread_join(data.output_sender_threads[index], NULL);
    for (unsigned index = 0; index < TRACTUS_NDI_RECEIVER_COUNT; index++)
        pthread_join(data.receiver_threads[index], NULL);
    pthread_join(data.discovery_thread, NULL);
    pthread_join(data.control_thread, NULL);
    close(data.control_fd);
    unlink(data.socket_path);
    pw_filter_destroy(data.filter);
    pw_main_loop_destroy(data.loop);
    pw_deinit();
    data.ndi->destroy();
    dlclose(data.ndi_library);
    pthread_mutex_destroy(&data.configuration_mutex);
    pthread_mutex_destroy(&data.status_mutex);
    pthread_mutex_destroy(&data.sources_mutex);
    return 0;
}
