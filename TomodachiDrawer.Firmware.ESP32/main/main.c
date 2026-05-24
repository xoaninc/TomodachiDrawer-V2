#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"

#include "led_indicator.h"

#define LED_GPIO 48  // ESP32-S3-DevKitC-1 onboard NeoPixel (rev v1.1+).
                     // If your board reports no LED activity, try GPIO 38
                     // (older DevKitC-1 revs) or check the board's silkscreen.

static const char *TAG = "tomodachi";

void app_main(void) {
    ESP_LOGI(TAG, "TomodachiDrawer ESP32-S3 - LED indicator stage");
    led_init(LED_GPIO);
    led_idle();
    ESP_LOGI(TAG, "LED initialised on GPIO %d, idle dim-white", LED_GPIO);

    while (1) {
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}
