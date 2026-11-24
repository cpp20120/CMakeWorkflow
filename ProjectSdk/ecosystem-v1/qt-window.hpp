#pragma once

#include <QWidget>
#include <memory>

namespace Ui { class Window; }
class Window final : public QWidget {
    Q_OBJECT
public:
    Window();
    ~Window() override;
    QString text() const;
private:
    std::unique_ptr<Ui::Window> ui_;
};
