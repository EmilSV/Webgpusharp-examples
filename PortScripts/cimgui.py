import os

TAG = '1.91.6.1'

URL_SOURCE = 'https://github.com/EmilSV/imgui_net_port_source/releases/download/main-2c6ed35/cimgui-source-package.zip'
HASH = '76225136132becb011a64a10b6cb3e075ed5a994202c13d69527ba8c7d7e037dc02420621676a95c97842ba1c2e29141a3b60c02a873e20b14f3b6f0c13c770f'

# contrib port information (required)
URL = 'https://github.com/cimgui/cimgui/'
DESCRIPTION = 'CImGui is a C wrapper for the popular Dear ImGui library'
LICENSE = 'MIT License'

port_name = 'cimgui'
lib_name = 'libcimgui.a'


def get(ports, settings, shared):
  # get the port
  ports.fetch_project(port_name, URL_SOURCE, sha512hash=HASH)

  def create(final):
    root_path = os.path.join(ports.get_dir(), port_name, f'cimgui-{TAG}')
    source_path = os.path.join(root_path, 'cimgui')
    cimgui_path = source_path
    imgui_path = os.path.join(root_path, 'imgui')

    cimgui_include = ['cimconfig.h', 'cimgui.h']
    imgui_include = ['imconfig.h', 'imgui.h', 'imgui_internal.h', 'imstb_rectpack.h', 'imstb_textedit.h', 'imstb_truetype.h']

    includes = []
    for inc in cimgui_include:
      includes.append(os.path.join(cimgui_path, inc))

    for inc in imgui_include:
      includes.append(os.path.join(imgui_path, inc))
            
    srcs_cimgui = ['cimgui.cpp']
    srcs_imgui = ['imgui_demo.cpp', 'imgui_draw.cpp','imgui_tables.cpp', 'imgui_widgets.cpp','imgui.cpp']

    srcs = []
    for src in srcs_cimgui:
        srcs.append(src)
    for src in srcs_imgui:
        srcs.append(os.path.join('imgui', src))

    ports.build_port(source_path, final, port_name, includes=includes, srcs=srcs)

  return [shared.cache.get_lib(lib_name, create, what='port')]

    
def clear(ports, settings, shared):
  shared.cache.erase_lib(lib_name)