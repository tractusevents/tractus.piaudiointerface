#include "tractus-audio-dsp.h"

#include <pipewire/keys.h>
#include <spa/param/audio/raw.h>

#include <signal.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

static void do_quit(void *userdata, int signal_number)
{
    (void)signal_number;
    struct tractus_dsp_data *data = userdata;
    pw_main_loop_quit(data->loop);
}

static int add_input_ports(struct tractus_dsp_data *data)
{
    for (unsigned device = 0; device < TRACTUS_DSP_DEVICE_COUNT; device++) {
        for (unsigned channel = 0; channel < TRACTUS_DSP_CHANNEL_COUNT; channel++) {
            char port_name[32];
            snprintf(port_name, sizeof(port_name), "input_%u_%s",
                device + 1, channel == 0 ? "FL" : "FR");
            data->input_ports[device][channel] = pw_filter_add_port(
                data->filter,
                PW_DIRECTION_INPUT,
                PW_FILTER_PORT_FLAG_MAP_BUFFERS,
                sizeof(struct tractus_dsp_port),
                pw_properties_new(
                    PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                    PW_KEY_PORT_NAME, port_name,
                    SPA_KEY_AUDIO_CHANNEL, channel == 0 ? "FL" : "FR",
                    NULL),
                NULL,
                0);
            if (data->input_ports[device][channel] == NULL) {
                fprintf(stderr, "Could not create DSP input port %s\n", port_name);
                return -1;
            }
        }
    }

    data->input_ports[TRACTUS_DSP_SIDETONE_SOURCE][0] = pw_filter_add_port(
        data->filter,
        PW_DIRECTION_INPUT,
        PW_FILTER_PORT_FLAG_MAP_BUFFERS,
        sizeof(struct tractus_dsp_port),
        pw_properties_new(
            PW_KEY_FORMAT_DSP, "32 bit float mono audio",
            PW_KEY_PORT_NAME, "input_sidetone_MONO",
            SPA_KEY_AUDIO_CHANNEL, "MONO",
            NULL),
        NULL,
        0);
    if (data->input_ports[TRACTUS_DSP_SIDETONE_SOURCE][0] == NULL) {
        fprintf(stderr, "Could not create DSP sidetone input port\n");
        return -1;
    }

    for (unsigned channel = 0; channel < TRACTUS_DSP_CHANNEL_COUNT; channel++) {
        char port_name[32];
        snprintf(port_name, sizeof(port_name), "input_ndi_%s",
            channel == 0 ? "FL" : "FR");
        data->input_ports[TRACTUS_DSP_NDI_RECEIVER_SOURCE][channel] =
            pw_filter_add_port(
                data->filter,
                PW_DIRECTION_INPUT,
                PW_FILTER_PORT_FLAG_MAP_BUFFERS,
                sizeof(struct tractus_dsp_port),
                pw_properties_new(
                    PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                    PW_KEY_PORT_NAME, port_name,
                    SPA_KEY_AUDIO_CHANNEL, channel == 0 ? "FL" : "FR",
                    NULL),
                NULL,
                0);
        if (data->input_ports[TRACTUS_DSP_NDI_RECEIVER_SOURCE][channel] == NULL) {
            fprintf(stderr, "Could not create DSP NDI input port %s\n", port_name);
            return -1;
        }
    }
    return 0;
}

static int add_output_ports(struct tractus_dsp_data *data)
{
    for (unsigned channel = 0; channel < TRACTUS_DSP_CHANNEL_COUNT; channel++) {
        char port_name[32];
        snprintf(port_name, sizeof(port_name), "output_%s",
            channel == 0 ? "FL" : "FR");
        data->output_ports[channel] = pw_filter_add_port(
            data->filter,
            PW_DIRECTION_OUTPUT,
            PW_FILTER_PORT_FLAG_MAP_BUFFERS,
            sizeof(struct tractus_dsp_port),
            pw_properties_new(
                PW_KEY_FORMAT_DSP, "32 bit float mono audio",
                PW_KEY_PORT_NAME, port_name,
                SPA_KEY_AUDIO_CHANNEL, channel == 0 ? "FL" : "FR",
                NULL),
            NULL,
            0);
        if (data->output_ports[channel] == NULL) {
            fprintf(stderr, "Could not create DSP output port %s\n", port_name);
            return -1;
        }
    }
    return 0;
}

int main(int argc, char *argv[])
{
    struct tractus_dsp_data data = { 0 };
    data.control_fd = -1;
    data.sample_rate = TRACTUS_DSP_DEFAULT_SAMPLE_RATE;
    data.master_gain_state = 1.0f;
    data.duck_gain_state = 1.0f;
    atomic_init(&data.running, true);
    atomic_init(&data.meter_write_sequence, 0);
    tractus_dsp_parameters_initialize(&data.parameters);

    pw_init(&argc, &argv);
    data.loop = pw_main_loop_new(NULL);
    if (data.loop == NULL) {
        fprintf(stderr, "Could not create PipeWire main loop\n");
        pw_deinit();
        return 1;
    }
    pw_loop_add_signal(pw_main_loop_get_loop(data.loop), SIGINT, do_quit, &data);
    pw_loop_add_signal(pw_main_loop_get_loop(data.loop), SIGTERM, do_quit, &data);

    data.filter = pw_filter_new_simple(
        pw_main_loop_get_loop(data.loop),
        "tractus_audio_dsp",
        pw_properties_new(
            PW_KEY_NODE_NAME, "tractus_audio_dsp",
            PW_KEY_NODE_DESCRIPTION, "Tractus Audio DSP Mixer",
            PW_KEY_MEDIA_TYPE, "Audio",
            PW_KEY_MEDIA_CATEGORY, "Filter",
            PW_KEY_MEDIA_ROLE, "DSP",
            NULL),
        &tractus_dsp_filter_events,
        &data);
    if (data.filter == NULL) {
        fprintf(stderr, "Could not create PipeWire DSP filter\n");
        pw_main_loop_destroy(data.loop);
        pw_deinit();
        return 1;
    }

    if (add_input_ports(&data) < 0 || add_output_ports(&data) < 0)
        return 1;
    if (tractus_dsp_create_control_socket(&data) < 0)
        return 1;
    if (pthread_create(
            &data.control_thread, NULL, tractus_dsp_control_thread_main, &data) != 0) {
        perror("pthread_create");
        close(data.control_fd);
        unlink(data.socket_path);
        return 1;
    }
    if (pw_filter_connect(data.filter, PW_FILTER_FLAG_RT_PROCESS, NULL, 0) < 0) {
        fprintf(stderr, "Could not connect PipeWire DSP filter\n");
        atomic_store(&data.running, false);
        pthread_join(data.control_thread, NULL);
        close(data.control_fd);
        unlink(data.socket_path);
        return 1;
    }

    fprintf(stderr, "Tractus Audio DSP ready; control socket %s\n", data.socket_path);
    pw_main_loop_run(data.loop);

    atomic_store(&data.running, false);
    pthread_join(data.control_thread, NULL);
    close(data.control_fd);
    unlink(data.socket_path);
    pw_filter_destroy(data.filter);
    pw_main_loop_destroy(data.loop);
    pw_deinit();
    return 0;
}
