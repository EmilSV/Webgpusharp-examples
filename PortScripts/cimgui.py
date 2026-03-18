import os

TAG = '1.91.6.1'

URL_SOURCE = 'https://github.com/EmilSV/imgui_net_port_source/releases/download/main-36801aa/cimgui-source-package.zip'
HASH = 'b0479fb44aac12edbfd49f1d94e1d904822d9eb839136b908c0c8b3a0916c8036154af459b2c6d7ad5c9ce6a1cd1e0c89209e4ef96d0b16f676604f8eaf0f3b5'

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