#include "window.hpp"
#include "ui_window.h"

#include <QFile>

Window::Window() : ui_(std::make_unique<Ui::Window>()) {
    ui_->setupUi(this);
    QFile resource(QStringLiteral(":/cmwf/banner.txt"));
    if (resource.open(QIODevice::ReadOnly)) {
        ui_->banner->setText(QString::fromUtf8(resource.readAll()).trimmed());
    }
}

Window::~Window() = default;
QString Window::text() const { return ui_->banner->text(); }
