#include <vulkan/vulkan.h>
#include <array>
#include <fstream>
#include <string_view>

int main(int argc, char** argv) {
    if (argc != 2) return 1;
    if (std::string_view(argv[1]) == "--vulkan") {
        VkInstanceCreateInfo create{};
        create.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
        VkInstance instance{};
        if (vkCreateInstance(&create, nullptr, &instance) != VK_SUCCESS) return 2;
        vkDestroyInstance(instance, nullptr);
        return 0;
    }
    std::ifstream input(argv[1], std::ios::binary);
    std::array<unsigned char, 4> bytes{};
    input.read(reinterpret_cast<char*>(bytes.data()), bytes.size());
    return input && bytes == std::array<unsigned char, 4>{3, 2, 35, 7} ? 0 : 3;
}
