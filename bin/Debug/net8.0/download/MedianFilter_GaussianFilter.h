#ifndef FLOW_SIGNAL_PROCESSOR_H
#define FLOW_SIGNAL_PROCESSOR_H

#ifdef __cplusplus
extern "C" {
#endif

int MedianFilter_GaussianFilter_process(double* input, int length, double* output);

#ifdef __cplusplus
}
#endif

#endif // FLOW_SIGNAL_PROCESSOR_H
