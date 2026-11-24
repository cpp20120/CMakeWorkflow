#include "window.hpp"

#include <QApplication>
#include <QTimer>

int main(int argc, char** argv) {
    QApplication app(argc, argv);
    Window window;
    window.show();
    if (app.arguments().contains(QStringLiteral("--smoke"))) {
        QTimer::singleShot(0, &app, [&] { app.exit(window.text().isEmpty() ? 1 : 0); });
    }
    return app.exec();
}
