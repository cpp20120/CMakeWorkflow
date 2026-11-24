#include "window.hpp"
#include <QtTest>

class Smoke final : public QObject {
    Q_OBJECT
private slots:
    void readsCompiledResource() {
        Window window;
        QCOMPARE(window.text(), QStringLiteral("CMakeWorkflow Qt resource"));
    }
};
QTEST_MAIN(Smoke)
#include "smoke.moc"
