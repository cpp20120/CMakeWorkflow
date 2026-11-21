#include <stdio.h>

int main(int argc, char **argv)
{
    volatile unsigned long sum = 0;
    for (unsigned long i = 0; i < 1000000; ++i) {
        sum += i;
    }
    if (argc == 2) {
        FILE *effects = fopen(argv[1], "a");
        if (effects == NULL) {
            perror("benchmark effect log");
            return 1;
        }
        fputs("timing\n", effects);
        if (fclose(effects) != 0) {
            return 1;
        }
    }
    printf("approved revision, checksum %lu\n", sum);
    return 0;
}
