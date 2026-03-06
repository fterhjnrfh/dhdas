#ifndef MEDIANFILTER_H
#define MEDIANFILTER_H

#ifdef __cplusplus
extern "C" {
#endif

int medianfilter_process(double* input, int length, double* output);

#ifdef __cplusplus
}
#endif

#endif // MEDIANFILTER_H
