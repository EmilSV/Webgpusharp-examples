# Copyright (c) 2024 pongasoft
#
# Licensed under the MIT License. You may obtain a copy of the License at
#
# https://opensource.org/licenses/MIT
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
# WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
# License for the specific language governing permissions and limitations under
# the License.
#
# @author Yan Pujante

import glob
import os
import re
from typing import Union, Dict, Optional

# contrib port information (required)
URL = 'https://github.com/cimgui/cimgui'
DESCRIPTION = 'cimgui: C API wrapper for Dear ImGui'
LICENSE = 'MIT License'

VALID_OPTION_VALUES = {
    'renderer': ['opengl3', 'wgpu'],
    'backend': ['sdl2', 'glfw'],
    'branch': ['master', 'docking'],
    'disableDemo': ['true', 'false'],
    'disableImGuiStdLib': ['true', 'false'],
    'disableDefaultFont': ['true', 'false'],
    'optimizationLevel': ['0', '1', '2', '3', 'g', 's', 'z']  # all -OX possibilities
}

# key is backend, value is set of possible renderers
VALID_RENDERERS = {
    'glfw': {'opengl3', 'wgpu'},
    'sdl2': {'opengl3'}
}

OPTIONS = {
    'imguiSha': 'Which Dear ImGui commit to use: full 40-character git SHA (required)',
    'imguiArchiveHash': 'Optional SHA-512 checksum for the Dear ImGui GitHub archive zip',
    'cimguiSha': 'Which cimgui commit to use: full 40-character git SHA (required)',
    'cimguiArchiveHash': 'Optional SHA-512 checksum for the cimgui GitHub archive zip',
    'renderer': f'Which renderer to use: {VALID_OPTION_VALUES["renderer"]} (required)',
    'backend': f'Which backend to use: {VALID_OPTION_VALUES["backend"]} (required)',
    'branch': 'Which branch flavor to use: master or docking (default to master)',
    'disableDemo': 'A boolean to disable ImGui demo (enabled by default)',
    'disableImGuiStdLib': 'A boolean to disable misc/cpp/imgui_stdlib.cpp (enabled by default)',
    'disableDefaultFont': 'A boolean to disable the default font (enabled by default)',
    'optimizationLevel': f'Optimization level: {VALID_OPTION_VALUES["optimizationLevel"]} (default to 2)',
}

# user options (from --use-port)
opts: Dict[str, Union[Optional[str], bool]] = {
    'imguiSha': None,
    'imguiArchiveHash': None,
    'cimguiSha': None,
    'cimguiArchiveHash': None,
    'renderer': None,
    'backend': None,
    'branch': 'master',
    'disableDemo': False,
    'disableImGuiStdLib': False,
    'disableDefaultFont': False,
    'optimizationLevel': '2'
}

deps = []

port_name = 'cimgui'
imgui_project_name = 'imgui-source'
cimgui_project_name = 'cimgui-source'


def get_imgui_sha():
    return opts['imguiSha']


def get_cimgui_sha():
    return opts['cimguiSha']


def get_imgui_zip_url():
    return f'https://github.com/ocornut/imgui/archive/{get_imgui_sha()}.zip'


def get_cimgui_zip_url():
    return f'https://github.com/cimgui/cimgui/archive/{get_cimgui_sha()}.zip'


def get_project_root_path(ports, project_name, prefix, marker):
    candidates = sorted(glob.glob(os.path.join(ports.get_dir(), project_name, f'{prefix}-*')))
    for candidate in candidates:
        if os.path.isfile(os.path.join(candidate, marker)):
            return candidate
    return os.path.join(ports.get_dir(), project_name, f'{prefix}-unknown')


def get_imgui_root_path(ports):
    return get_project_root_path(ports, imgui_project_name, 'imgui', 'imgui.cpp')


def get_cimgui_root_path(ports):
    return get_project_root_path(ports, cimgui_project_name, 'cimgui', 'cimgui.cpp')


def get_lib_name(settings):
    return (f'lib_{port_name}_{get_cimgui_sha()}-{get_imgui_sha()}-{opts["branch"]}-{opts["backend"]}-{opts["renderer"]}-O{opts["optimizationLevel"]}' +
            ('-nd' if opts['disableDemo'] else '') +
            ('-nl' if opts['disableImGuiStdLib'] else '') +
            ('-nf' if opts['disableDefaultFont'] else '') +
            '.a')


def get(ports, settings, shared):
    from tools import utils

    if opts['imguiSha'] is None or opts['cimguiSha'] is None or opts['backend'] is None or opts['renderer'] is None:
        utils.exit_with_error('cimgui port requires imguiSha, cimguiSha, backend and renderer options to be defined')

    if opts['imguiArchiveHash']:
        ports.fetch_project(imgui_project_name, get_imgui_zip_url(), sha512hash=opts['imguiArchiveHash'])
    else:
        ports.fetch_project(imgui_project_name, get_imgui_zip_url())

    if opts['cimguiArchiveHash']:
        ports.fetch_project(cimgui_project_name, get_cimgui_zip_url(), sha512hash=opts['cimguiArchiveHash'])
    else:
        ports.fetch_project(cimgui_project_name, get_cimgui_zip_url())

    def create(final):
        imgui_root_path = get_imgui_root_path(ports)
        cimgui_root_path = get_cimgui_root_path(ports)
        source_path = ports.get_dir()

        # this port does not install the headers on purpose (see process_args)
        # a) there is no need (simply refer to the fetched content)
        # b) allows the wrapper and Dear ImGui sources to come from separate exact SHAs

        srcs = [
            os.path.relpath(os.path.join(cimgui_root_path, 'cimgui.cpp'), source_path),
            os.path.relpath(os.path.join(imgui_root_path, 'imgui.cpp'), source_path),
            os.path.relpath(os.path.join(imgui_root_path, 'imgui_draw.cpp'), source_path),
            os.path.relpath(os.path.join(imgui_root_path, 'imgui_tables.cpp'), source_path),
            os.path.relpath(os.path.join(imgui_root_path, 'imgui_widgets.cpp'), source_path),
        ]
        if not opts['disableDemo']:
            srcs.append(os.path.relpath(os.path.join(imgui_root_path, 'imgui_demo.cpp'), source_path))
        if not opts['disableImGuiStdLib']:
            srcs.append(os.path.relpath(os.path.join(imgui_root_path, 'misc', 'cpp', 'imgui_stdlib.cpp'), source_path))
        srcs.append(os.path.relpath(os.path.join(imgui_root_path, 'backends', f'imgui_impl_{opts["backend"]}.cpp'), source_path))
        srcs.append(os.path.relpath(os.path.join(imgui_root_path, 'backends', f'imgui_impl_{opts["renderer"]}.cpp'), source_path))

        flags = [f'--use-port={value}' for value in deps]
        flags.append(f'-O{opts["optimizationLevel"]}')
        flags.append('-Wno-nontrivial-memaccess')

        if opts['disableDefaultFont']:
            flags.append('-DIMGUI_DISABLE_DEFAULT_FONT')

        ports.build_port(source_path, final, port_name, srcs=srcs, flags=flags)

    lib = shared.cache.get_lib(get_lib_name(settings), create, what='port')
    if os.path.getmtime(lib) < os.path.getmtime(__file__):
        clear(ports, settings, shared)
        lib = shared.cache.get_lib(get_lib_name(settings), create, what='port')
    return [lib]


def clear(ports, settings, shared):
    shared.cache.erase_lib(get_lib_name(settings))


def process_args(ports):
    args = ['-I', get_cimgui_root_path(ports), '-I', get_imgui_root_path(ports)]
    if opts['branch'] == 'docking':
        args += ['-DIMGUI_ENABLE_DOCKING=1']
    if opts['disableDemo']:
        args += ['-DIMGUI_DISABLE_DEMO=1']
    return args


def linker_setup(ports, settings):
    if opts['backend'] == 'glfw':
        settings.MIN_WEBGL_VERSION = 2
        settings.MAX_WEBGL_VERSION = 2


def check_option(option, value, error_handler):
    if option == 'imguiSha' or option == 'cimguiSha':
        if not re.fullmatch(r'[0-9a-f]{40}', value):
            error_handler(f'[{option}] must be a full 40-character git SHA, got [{value}]')
        return value
    if option == 'imguiArchiveHash' or option == 'cimguiArchiveHash':
        if not re.fullmatch(r'[0-9a-f]{128}', value):
            error_handler(f'[{option}] must be a 128-character SHA-512 hex digest, got [{value}]')
        return value
    if value not in VALID_OPTION_VALUES[option]:
        error_handler(f'[{option}] can be {list(VALID_OPTION_VALUES[option])}, got [{value}]')
    if isinstance(opts[option], bool):
        value = value == 'true'
    return value


def check_required_option(option, value, error_handler):
    if opts[option] is not None and opts[option] != value:
        error_handler(f'[{option}] is already set with incompatible value [{opts[option]}]')
    return check_option(option, value, error_handler)


def handle_options(options, error_handler):
    deps.clear()
    for option, value in options.items():
        value = value.lower()
        if option == 'renderer' or option == 'backend':
            opts[option] = check_required_option(option, value, error_handler)
        else:
            opts[option] = check_option(option, value, error_handler)

    if opts['imguiSha'] is None or opts['cimguiSha'] is None or opts['backend'] is None or opts['renderer'] is None:
        error_handler('imguiSha, cimguiSha, backend and renderer options must be defined')

    if opts['renderer'] not in VALID_RENDERERS[opts['backend']]:
        error_handler(f'backend [{opts["backend"]}] does not support [{opts["renderer"]}] renderer')

    if opts['backend'] == 'glfw':
        glfw3_options = {'optimizationLevel': opts['optimizationLevel']}
        if opts['renderer'] == 'wgpu':
          glfw3_options['disableWebGL2'] = 'true'
          deps.append('emdawnwebgpu')
        glfw3_options = ':'.join(f"{key}={value}" for key, value in glfw3_options.items())
        deps.append(f"contrib.glfw3:{glfw3_options}")
    else:
        deps.append('sdl2')

if __name__ == "__main__":
    print(f'''# To compute checksums run this
# example
IMGUI_SHA=<full_sha>
CIMGUI_SHA=<full_sha>
curl -sfL https://github.com/ocornut/imgui/archive/$IMGUI_SHA.zip | shasum -a 512
curl -sfL https://github.com/cimgui/cimgui/archive/$CIMGUI_SHA.zip | shasum -a 512
''')