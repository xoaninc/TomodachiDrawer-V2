#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"

static const char *TAG = "tomodachi";

void app_main(void) {
    ESP_LOGI(TAG, "TomodachiDrawer ESP32-S3 firmware - scaffold boot OK");
    while (1) {
        ESP_LOGI(TAG, "alive");
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}
