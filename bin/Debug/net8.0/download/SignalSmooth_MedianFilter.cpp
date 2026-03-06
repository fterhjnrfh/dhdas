#include <vector>
#include <cmath>
#include <algorithm>
#include <cstring>

std::vector<double> generate_gaussian_kernel(int size, double sigma) {
    std::vector<double> kernel(size);
    int center = size / 2;
    double sum = 0.0;
    
    for (int i = 0; i < size; i++) {
        double x = i - center;
        kernel[i] = exp(-(x * x) / (2 * sigma * sigma));
        sum += kernel[i];
    }
    
    // 归一化
    for (int i = 0; i < size; i++) {
        kernel[i] /= sum;
    }
    
    return kernel;
}

extern "C" {
    __declspec(dllexport) int SignalSmooth_MedianFilter_process(double* input, int length, double* output) {
        if (!input || !output || length <= 0) {
            return -1; // 错误
        }
        
        // 创建临时数组用于中间处理
        std::vector<double> temp_input(input, input + length);
        std::vector<double> temp_output(length);
        std::vector<double> swap_temp;
        
        // 步骤 1: 信号平滑
        {
            int window_size = 5;
            double smooth_factor = 0.5;
            int half_window = window_size / 2;
            
            for (int i = 0; i < length; i++) {
                double sum = 0.0;
                double weight_sum = 0.0;
                
                for (int j = 0; j < window_size; j++) {
                    int idx = i - half_window + j;
                    double distance = abs(j - half_window) / (double)half_window;
                    double weight = 1.0 - (smooth_factor * distance);
                    
                    // 边界处理
                    if (idx < 0) idx = -idx;
                    else if (idx >= length) idx = 2 * length - idx - 2;
                    
                    if (idx >= 0 && idx < length) {
                        sum += temp_input[idx] * weight;
                        weight_sum += weight;
                    }
                }
                
                if (weight_sum > 0) {
                    temp_output[i] = sum / weight_sum;
                } else {
                    temp_output[i] = temp_input[i]; // 保持原值
                }
            }
            
            // 交换输入输出数组
            swap_temp = temp_input;
            temp_input = temp_output;
            temp_output = swap_temp;
        }
        
        // 步骤 2: 中值滤波
        {
            int window_size = 3;
            int half_window = window_size / 2;
            std::vector<double> window(window_size);
            
            for (int i = 0; i < length; i++) {
                // 构建窗口
                for (int j = 0; j < window_size; j++) {
                    int idx = i - half_window + j;
                    
                    // 边界处理
                    if (idx < 0) idx = -idx;
                    else if (idx >= length) idx = 2 * length - idx - 2;
                    
                    if (idx >= 0 && idx < length) {
                        window[j] = temp_input[idx];
                    } else {
                        window[j] = 0.0; // 默认值
                    }
                }
                
                // 排序并取中值
                std::sort(window.begin(), window.end());
                temp_output[i] = window[window_size / 2];
            }
            
            // 交换输入输出数组
            swap_temp = temp_input;
            temp_input = temp_output;
            temp_output = swap_temp;
        }
        
        // 将最终结果复制到输出数组
        for (int i = 0; i < length; i++) {
            output[i] = temp_input[i];
        }
        
        return 0; // 成功
    }
}
