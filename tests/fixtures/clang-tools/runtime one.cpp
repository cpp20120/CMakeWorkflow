#ifdef WRONG_TARGET
#error The other target's compilation command must not run.
#endif
#include "runtime.hpp"
int one(){return two();}
