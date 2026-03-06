#include <vector>
#include <cmath>
#include <algorithm>
#include <cstring>

// 中值滤波函数
int median_filter(double* input, int length, double* output) {
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
                window[j] = input[idx];
            } else {
                window[j] = 0.0; // 默认值
            }
        }
        
        // 排序并取中值
        std::sort(window.begin(), window.end());
        output[i] = window[window_size / 2];
    }
    
    return 0; // 成功
}

extern "C" {
    __declspec(dllexport) int medianfilter_process(double* input, int length, double* output) {
        if (!input || !output || length <= 0) {
            return -1; // 错误
        }
        
        return median_filter(input, length, output);
    }
}
