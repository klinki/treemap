# Initial findings

Before editing, all 85 existing tests passed in Release and all eight projects built. The complete test run used 154.1 MiB peak committed memory across its process tree. Kernel caps of 512 MiB per process and 1 GiB per job, whole-job termination, and deadlines were validated.

Confirmed reproductions include post-publication child mutation with stale snapshot lookup, incorrect parent logical totals, zero intermediate animation progress, no final repaint, 4,095 child traversals for a 12-folder chain, reparented child painted before its entering parent, hover selecting a different node from the visible cell, and publication leaving the frame unchanged.

Source inspection also establishes that the publisher's volatile store lies outside its writer lock, the synthetic ID counter is global, solution membership omits perf projects, resizing recomputes fixed-coordinate layouts, and the timer runs while idle. A Task.Wait timeout leaves its generation task running. LayoutTween.Evaluate reuses its supplied list; its per-frame sort is the actual recurring cost.

Space-bunny's source review supported the diagnoses but proposed incorrect numeric and comparator fixes. The supervisor rejected those claims using C# arithmetic reproduction and draw-order reasoning. Acceptance requires independent diff review and regressions for behavior, not reliance on subagent assertions.

Evidence is the saved supervisor review, baseline test/build logs, bounded runner validation, and isolated review probes. Performance numbers are verification measurements, not BenchmarkDotNet throughput results.
