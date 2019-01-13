using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using static Avalonia.X11.XLib;
// ReSharper disable IdentifierTypo
// ReSharper disable StringLiteralTypo
namespace Avalonia.X11
{
    unsafe class X11Window : IWindowImpl, IPopupImpl, IXI2Client
    {
        private readonly AvaloniaX11Platform _platform;
        private readonly bool _popup;
        private readonly X11Info _x11;
        private bool _invalidated;
        private XConfigureEvent? _configure;
        private IInputRoot _inputRoot;
        private readonly IMouseDevice _mouse;
        private readonly IKeyboardDevice _keyboard;
        private Point _position;
        private IntPtr _handle;
        private IntPtr _xic;
        private IntPtr _renderHandle;
        private bool _mapped;
        private HashSet<X11Window> _transientChildren = new HashSet<X11Window>();
        private X11Window _transientParent;

        class InputEventContainer
        {
            public RawInputEventArgs Event;
        }
        private readonly Queue<InputEventContainer> _inputQueue = new Queue<InputEventContainer>();
        private InputEventContainer _lastEvent;

        public X11Window(AvaloniaX11Platform platform, bool popup)
        {
            _platform = platform;
            _popup = popup;
            _x11 = platform.Info;
            _mouse = platform.MouseDevice;
            _keyboard = platform.KeyboardDevice;
            _xic = XCreateIC(_x11.Xim, XNames.XNInputStyle, XIMProperties.XIMPreeditNothing | XIMProperties.XIMStatusNothing,
                XNames.XNClientWindow, _handle, IntPtr.Zero);
            
            XSetWindowAttributes attr = new XSetWindowAttributes();
            var valueMask = default(SetWindowValuemask);

            attr.backing_store = 1;
            attr.bit_gravity = Gravity.NorthWestGravity;
            attr.win_gravity = Gravity.NorthWestGravity;
            valueMask |= SetWindowValuemask.BackPixel | SetWindowValuemask.BorderPixel
                         | SetWindowValuemask.BackPixmap | SetWindowValuemask.BackingStore
                         | SetWindowValuemask.BitGravity | SetWindowValuemask.WinGravity;

            if (popup)
            {
                attr.override_redirect = true;
                valueMask |= SetWindowValuemask.OverrideRedirect;
            }
            
            _handle = XCreateWindow(_x11.Display, _x11.RootWindow, 10, 10, 300, 200, 0,
                24,
                (int)CreateWindowArgs.InputOutput, IntPtr.Zero, 
                new UIntPtr((uint)valueMask), ref attr);
            _renderHandle = XCreateWindow(_x11.Display, _handle, 0, 0, 300, 200, 0, 24,
                (int)CreateWindowArgs.InputOutput,
                IntPtr.Zero,
                new UIntPtr((uint)(SetWindowValuemask.BorderPixel | SetWindowValuemask.BitGravity |
                                   SetWindowValuemask.WinGravity | SetWindowValuemask.BackingStore)), ref attr);
                
            Handle = new PlatformHandle(_handle, "XID");
            ClientSize = new Size(400, 400);
            platform.Windows[_handle] = OnEvent;
            XEventMask ignoredMask = XEventMask.SubstructureRedirectMask
                                     | XEventMask.ResizeRedirectMask
                                     | XEventMask.PointerMotionHintMask;
            if (platform.XI2 != null)
                ignoredMask |= platform.XI2.AddWindow(_handle, this);
            var mask = new IntPtr(0xffffff ^ (int)ignoredMask);
            XSelectInput(_x11.Display, _handle, mask);
            var protocols = new[]
            {
                _x11.Atoms.WM_DELETE_WINDOW
            };
            XSetWMProtocols(_x11.Display, _handle, protocols, protocols.Length);
            var feature = (EglGlPlatformFeature)AvaloniaLocator.Current.GetService<IWindowingPlatformGlFeature>();
            var surfaces = new List<object>
            {
                new X11FramebufferSurface(_x11.DeferredDisplay, _handle)
            };
            if (feature != null)
                surfaces.Insert(0,
                    new EglGlPlatformSurface((EglDisplay)feature.Display, feature.DeferredContext,
                        new SurfaceInfo(_x11.DeferredDisplay, _handle, _renderHandle)));
            Surfaces = surfaces.ToArray();
            UpdateMotifHits();
            XFlush(_x11.Display);
        }

        class SurfaceInfo  : EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo
        {
            private readonly IntPtr _display;
            private readonly IntPtr _parent;

            public SurfaceInfo(IntPtr display, IntPtr parent, IntPtr xid)
            {
                _display = display;
                _parent = parent;
                Handle = xid;
            }
            public IntPtr Handle { get; }

            public PixelSize Size
            {
                get
                {
                    XLockDisplay(_display);
                    XGetGeometry(_display, _parent, out var geo);
                    XResizeWindow(_display, Handle, geo.width, geo.height);
                    XFlush(_display);
                    XSync(_display, true);
                    XUnlockDisplay(_display);
                    return new PixelSize(geo.width, geo.height);
                }
            }

            public double Scaling { get; } = 1;
        }

        void UpdateMotifHits()
        {
            var functions = MotifFunctions.All;
            var decorations = MotifDecorations.All;

            if (_popup || !_systemDecorations)
            {
                decorations = 0;
            }

            if (!_canResize)
            {
                functions ^= MotifFunctions.Resize | MotifFunctions.Maximize;
                decorations ^= MotifDecorations.Maximize | MotifDecorations.ResizeH;
            }

            var hints = new MotifWmHints
            {
                flags = new IntPtr((int)(MotifFlags.Decorations | MotifFlags.Functions)),
                decorations = new IntPtr((int)decorations),
                functions = new IntPtr((int)functions)
            };


            XChangeProperty(_x11.Display, _handle,
                _x11.Atoms._MOTIF_WM_HINTS, _x11.Atoms._MOTIF_WM_HINTS, 32,
                PropertyMode.Replace, ref hints, 5);
        }

        void UpdateSizeHits()
        {
            var hints = new XSizeHints
            {
                min_width = (int)_minMaxSize.minSize.Width,
                min_height = (int)_minMaxSize.minSize.Height
            };
            hints.height_inc = hints.width_inc = 1;
            var flags = XSizeHintsFlags.PMinSize | XSizeHintsFlags.PResizeInc;
            // People might be passing double.MaxValue
            if (_minMaxSize.maxSize.Width < 100000 && _minMaxSize.maxSize.Height < 100000)
            {

                hints.max_width = (int)Math.Max(100000, _minMaxSize.maxSize.Width);
                hints.max_height = (int)Math.Max(100000, _minMaxSize.maxSize.Height);
                flags |= XSizeHintsFlags.PMaxSize;
            }

            hints.flags = (IntPtr)flags;

            XSetWMNormalHints(_x11.Display, _handle, ref hints);
        }
        
        public Size ClientSize { get; private set; }
        //TODO
        public double Scaling { get; } = 1;
        public IEnumerable<object> Surfaces { get; }
        public Action<RawInputEventArgs> Input { get; set; }
        public Action<Rect> Paint { get; set; }
        public Action<Size> Resized { get; set; }
        //TODO
        public Action<double> ScalingChanged { get; set; }
        public Action Deactivated { get; set; }
        public Action Activated { get; set; }
        public Func<bool> Closing { get; set; }
        public Action<WindowState> WindowStateChanged { get; set; }
        public Action Closed { get; set; }
        public Action<Point> PositionChanged { get; set; }

        public IRenderer CreateRenderer(IRenderRoot root) =>
            new DeferredRenderer(root, AvaloniaLocator.Current.GetService<IRenderLoop>());

        void OnEvent(XEvent ev)
        {
            if(XFilterEvent(ref ev, _handle))
                return;
            if (ev.type == XEventName.MapNotify)
            {
                _mapped = true;
                XMapWindow(_x11.Display, _renderHandle);
            }
            else if (ev.type == XEventName.UnmapNotify)
                _mapped = false;
            else if (ev.type == XEventName.Expose)
                DoPaint();
            else if (ev.type == XEventName.FocusIn)
            {
                if (ActivateTransientChildIfNeeded())
                    return;
                Activated?.Invoke();
            }
            else if (ev.type == XEventName.FocusOut)
                Deactivated?.Invoke();
            else if (ev.type == XEventName.MotionNotify)
                MouseEvent(RawMouseEventType.Move, ref ev, ev.MotionEvent.state);
            else if (ev.type == XEventName.LeaveNotify)
                MouseEvent(RawMouseEventType.LeaveWindow, ref ev, ev.CrossingEvent.state);
            else if (ev.type == XEventName.PropertyNotify)
            {
                OnPropertyChange(ev.PropertyEvent.atom, ev.PropertyEvent.state == 0);
            }
            else if (ev.type == XEventName.ButtonPress)
            {
                if (ActivateTransientChildIfNeeded())
                    return;
                if (ev.ButtonEvent.button < 4)
                    MouseEvent(ev.ButtonEvent.button == 1 ? RawMouseEventType.LeftButtonDown
                        : ev.ButtonEvent.button == 2 ? RawMouseEventType.MiddleButtonDown
                        : RawMouseEventType.RightButtonDown, ref ev, ev.ButtonEvent.state);
                else
                {
                    var delta = ev.ButtonEvent.button == 4
                        ? new Vector(0, 1)
                        : ev.ButtonEvent.button == 5
                            ? new Vector(0, -1)
                            : ev.ButtonEvent.button == 6
                                ? new Vector(1, 0)
                                : new Vector(-1, 0);
                    ScheduleInput(new RawMouseWheelEventArgs(_mouse, (ulong)ev.ButtonEvent.time.ToInt64(),
                        _inputRoot, new Point(ev.ButtonEvent.x, ev.ButtonEvent.y), delta,
                        TranslateModifiers(ev.ButtonEvent.state)), ref ev);
                }
                
            }
            else if (ev.type == XEventName.ButtonRelease)
            {
                if (ev.ButtonEvent.button < 4)
                    MouseEvent(ev.ButtonEvent.button == 1 ? RawMouseEventType.LeftButtonUp
                        : ev.ButtonEvent.button == 2 ? RawMouseEventType.MiddleButtonUp
                        : RawMouseEventType.RightButtonUp, ref ev, ev.ButtonEvent.state);
            }
            else if (ev.type == XEventName.ConfigureNotify)
            {
                var needEnqueue = (_configure == null);
                _configure = ev.ConfigureEvent;
                if (needEnqueue)
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_configure == null)
                            return;
                        var cev = _configure.Value;
                        _configure = null;
                        var nsize = new Size(cev.width, cev.height);
                        XTranslateCoordinates(_x11.Display, _handle, _x11.DefaultRootWindow, 0, 0, out var xret,
                            out var yret, out var _);
                        var npos = new Point(xret, yret);
                        var changedSize = ClientSize != nsize;
                        var changedPos = npos != _position;
                        ClientSize = nsize;
                        _position = npos;
                        if (changedSize)
                            Resized?.Invoke(nsize);
                        if (changedPos)
                            PositionChanged?.Invoke(npos);
                        Dispatcher.UIThread.RunJobs(DispatcherPriority.Layout);
                    }, DispatcherPriority.Layout);
            }
            else if (ev.type == XEventName.DestroyNotify)
            {
                Cleanup();
            }
            else if (ev.type == XEventName.ClientMessage)
            {
                if (ev.ClientMessageEvent.message_type == _x11.Atoms.WM_PROTOCOLS)
                {
                    if (ev.ClientMessageEvent.ptr1 == _x11.Atoms.WM_DELETE_WINDOW)
                    {
                        if (Closing?.Invoke() != true)
                            Dispose();
                    }

                }
            }
            else if (ev.type == XEventName.KeyPress || ev.type == XEventName.KeyRelease)
            {
                if (ActivateTransientChildIfNeeded())
                    return;
                var buffer = stackalloc byte[40];

                var latinKeysym = XKeycodeToKeysym(_x11.Display, ev.KeyEvent.keycode, 0);
                ScheduleInput(new RawKeyEventArgs(_keyboard, (ulong)ev.KeyEvent.time.ToInt64(),
                    ev.type == XEventName.KeyPress ? RawKeyEventType.KeyDown : RawKeyEventType.KeyUp,
                    X11KeyTransform.ConvertKey(latinKeysym), TranslateModifiers(ev.KeyEvent.state)), ref ev);

                if (ev.type == XEventName.KeyPress)
                {
                    var len = Xutf8LookupString(_xic, ref ev, buffer, 40, out _, out _);
                    if (len != 0)
                    {
                        var text = Encoding.UTF8.GetString(buffer, len);
                        if (text.Length == 1)
                        {
                            if (text[0] < ' ' || text[0] == 0x7f) //Control codes or DEL
                                return;
                        }
                        ScheduleInput(new RawTextInputEventArgs(_keyboard, (ulong)ev.KeyEvent.time.ToInt64(), text),
                            ref ev);
                    }
                }
            }
        }

        private WindowState _lastWindowState;
        public WindowState WindowState
        {
            get => _lastWindowState;
            set
            {
                _lastWindowState = value;
                if (value == WindowState.Minimized)
                {
                    XIconifyWindow(_x11.Display, _handle, _x11.DefaultScreen);
                }
                else if (value == WindowState.Maximized)
                {
                    SendNetWMMessage(_x11.Atoms._NET_WM_STATE, (IntPtr)0, _x11.Atoms._NET_WM_STATE_HIDDEN, IntPtr.Zero);
                    SendNetWMMessage(_x11.Atoms._NET_WM_STATE, (IntPtr)1, _x11.Atoms._NET_WM_STATE_MAXIMIZED_VERT,
                        _x11.Atoms._NET_WM_STATE_MAXIMIZED_HORZ);
                }
                else
                {
                    SendNetWMMessage(_x11.Atoms._NET_WM_STATE, (IntPtr)0, _x11.Atoms._NET_WM_STATE_HIDDEN, IntPtr.Zero);
                    SendNetWMMessage(_x11.Atoms._NET_WM_STATE, (IntPtr)0, _x11.Atoms._NET_WM_STATE_MAXIMIZED_VERT,
                        _x11.Atoms._NET_WM_STATE_MAXIMIZED_HORZ);
                }
            }
        }

        private void OnPropertyChange(IntPtr atom, bool hasValue)
        {
            if (atom == _x11.Atoms._NET_WM_STATE)
            {
                WindowState state = WindowState.Normal;
                if(hasValue)
                {

                    XGetWindowProperty(_x11.Display, _handle, _x11.Atoms._NET_WM_STATE, IntPtr.Zero, new IntPtr(256),
                        false, (IntPtr)Atom.XA_ATOM, out _, out _, out var nitems, out _,
                        out var prop);
                    int maximized = 0;
                    var pitems = (IntPtr*)prop.ToPointer();
                    for (var c = 0; c < nitems.ToInt32(); c++)
                    {
                        if (pitems[c] == _x11.Atoms._NET_WM_STATE_HIDDEN)
                        {
                            state = WindowState.Minimized;
                            break;
                        }

                        if (pitems[c] == _x11.Atoms._NET_WM_STATE_MAXIMIZED_HORZ ||
                            pitems[c] == _x11.Atoms._NET_WM_STATE_MAXIMIZED_VERT)
                        {
                            maximized++;
                            if (maximized == 2)
                            {
                                state = WindowState.Maximized;
                                break;
                            }
                        }
                    }
                    XFree(prop);
                }
                if (_lastWindowState != state)
                {
                    _lastWindowState = state;
                    WindowStateChanged?.Invoke(state);
                }
            }

        }


        InputModifiers TranslateModifiers(XModifierMask state)
        {
            var rv = default(InputModifiers);
            if (state.HasFlag(XModifierMask.Button1Mask))
                rv |= InputModifiers.LeftMouseButton;
            if (state.HasFlag(XModifierMask.Button2Mask))
                rv |= InputModifiers.RightMouseButton;
            if (state.HasFlag(XModifierMask.Button2Mask))
                rv |= InputModifiers.MiddleMouseButton;
            if (state.HasFlag(XModifierMask.ShiftMask))
                rv |= InputModifiers.Shift;
            if (state.HasFlag(XModifierMask.ControlMask))
                rv |= InputModifiers.Control;
            if (state.HasFlag(XModifierMask.Mod1Mask))
                rv |= InputModifiers.Alt;
            if (state.HasFlag(XModifierMask.Mod4Mask))
                rv |= InputModifiers.Windows;
            return rv;
        }
        
        private bool _systemDecorations = true;
        private bool _canResize = true;
        private (Size minSize, Size maxSize) _minMaxSize;

        void ScheduleInput(RawInputEventArgs args, ref XEvent xev)
        {
            _x11.LastActivityTimestamp = xev.ButtonEvent.time;
            ScheduleInput(args);
        }

        public void ScheduleInput(RawInputEventArgs args)
        {
            
            _lastEvent = new InputEventContainer() {Event = args};
            _inputQueue.Enqueue(_lastEvent);
            if (_inputQueue.Count == 1)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    while (_inputQueue.Count > 0)
                    {
                        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input + 1);
                        var ev = _inputQueue.Dequeue();
                        Input?.Invoke(ev.Event);
                    }
                }, DispatcherPriority.Input);
            }
        }
        
        void MouseEvent(RawMouseEventType type, ref XEvent ev, XModifierMask mods)
        {
            var mev = new RawMouseEventArgs(
                _mouse, (ulong)ev.ButtonEvent.time.ToInt64(), _inputRoot,
                type, new Point(ev.ButtonEvent.x, ev.ButtonEvent.y), TranslateModifiers(mods)); 
            if(type == RawMouseEventType.Move && _inputQueue.Count>0 && _lastEvent.Event is RawMouseEventArgs ma)
                if (ma.Type == RawMouseEventType.Move)
                {
                    _lastEvent.Event = mev;
                    return;
                }
            ScheduleInput(mev, ref ev);
        }

        void DoPaint()
        {
            _invalidated = false;
            Paint?.Invoke(new Rect());
        }
        
        public void Invalidate(Rect rect)
        {
            if(_invalidated)
                return;
            _invalidated = true;
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_mapped)
                    DoPaint();
            });
        }

        public IInputRoot InputRoot => _inputRoot;
        
        public void SetInputRoot(IInputRoot inputRoot)
        {
            _inputRoot = inputRoot;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                XDestroyWindow(_x11.Display, _handle);
                Cleanup();
            }
        }

        void Cleanup()
        {
            SetTransientParent(null, false);
            if (_xic != IntPtr.Zero)
            {
                XDestroyIC(_xic);
                _xic = IntPtr.Zero;
            }
            
            if (_handle != IntPtr.Zero)
            {
                XDestroyWindow(_x11.Display, _handle);
                _platform.Windows.Remove(_handle);
                _platform.XI2?.OnWindowDestroyed(_handle);
                _handle = IntPtr.Zero;
                Closed?.Invoke();
            }
            
            if (_renderHandle != IntPtr.Zero)
            {
                XDestroyWindow(_x11.Display, _renderHandle);
                _renderHandle = IntPtr.Zero;
            }
        }

        bool ActivateTransientChildIfNeeded()
        {
            if (_transientChildren.Count == 0)
                return false;
            var child = _transientChildren.First();
            if (!child.ActivateTransientChildIfNeeded())
                child.Activate();
            return true;
        }
        
        void SetTransientParent(X11Window window, bool informServer = true)
        {
            _transientParent?._transientChildren.Remove(this);
            _transientParent = window;
            _transientParent?._transientChildren.Add(this);
            if (informServer)
                XSetTransientForHint(_x11.Display, _handle, _transientParent?._handle ?? IntPtr.Zero);
        }

        public void Show()
        {
            SetTransientParent(null);
            ShowCore();
        }
        
        void ShowCore()
        {
            XMapWindow(_x11.Display, _handle);
            XFlush(_x11.Display);
        }

        public void Hide() => XUnmapWindow(_x11.Display, _handle);
        
        
        public Point PointToClient(Point point) => new Point(point.X - _position.X, point.Y - _position.Y);

        public Point PointToScreen(Point point) => new Point(point.X + _position.X, point.Y + _position.Y);
        
        public void SetSystemDecorations(bool enabled)
        {
            _systemDecorations = enabled;
            UpdateMotifHits();
        }
        
                
        public void Resize(Size clientSize)
        {
            if (clientSize == ClientSize)
                return;
            var changes = new XWindowChanges
            {
                width = (int)clientSize.Width,
                height = (int)clientSize.Height
            };
            var needResize = clientSize != ClientSize;
            XConfigureWindow(_x11.Display, _handle, ChangeWindowFlags.CWHeight | ChangeWindowFlags.CWWidth,
                ref changes);
            XFlush(_x11.Display);

            if (_popup && needResize)
            {
                ClientSize = clientSize;
                Resized?.Invoke(clientSize);
            }
        }
        
        public void CanResize(bool value)
        {
            _canResize = value;
            UpdateMotifHits();
        }

        public void SetCursor(IPlatformHandle cursor)
        {
            if (cursor == null)
                XDefineCursor(_x11.Display, _handle, _x11.DefaultCursor);
            else
            {
                if (cursor.HandleDescriptor != "XCURSOR")
                    throw new ArgumentException("Expected XCURSOR handle type");
                XDefineCursor(_x11.Display, _handle, cursor.Handle);
            }
        }

        public IPlatformHandle Handle { get; }
        
        public Point Position
        {
            get => _position;
            set
            {
                var changes = new XWindowChanges
                {
                    x = (int)value.X,
                    y = (int)value.Y
                };
                XConfigureWindow(_x11.Display, _handle, ChangeWindowFlags.CWX | ChangeWindowFlags.CWY,
                    ref changes);
                XFlush(_x11.Display);

            }
        }

        public IMouseDevice MouseDevice => _mouse;
       
        public void Activate()
        {
            if (_x11.Atoms._NET_ACTIVE_WINDOW != IntPtr.Zero)
            {
                SendNetWMMessage(_x11.Atoms._NET_ACTIVE_WINDOW, (IntPtr)1, _x11.LastActivityTimestamp,
                    IntPtr.Zero);
            }
            else
            {
                XRaiseWindow(_x11.Display, _handle);
                XSetInputFocus(_x11.Display, _handle, 0, IntPtr.Zero);
            }
        }


        public IScreenImpl Screen { get; } = AvaloniaLocator.CurrentMutable.GetService<IScreenImpl>();
        public Size MaxClientSize { get; } = new Size(1920, 1280);



        void SendNetWMMessage(IntPtr message_type, IntPtr l0,
            IntPtr? l1 = null, IntPtr? l2 = null, IntPtr? l3 = null, IntPtr? l4 = null)
        {
            var xev = new XEvent
            {
                ClientMessageEvent =
                {
                    type = XEventName.ClientMessage,
                    send_event = true,
                    window = _handle,
                    message_type = message_type,
                    format = 32,
                    ptr1 = l0,
                    ptr2 = l1 ?? IntPtr.Zero,
                    ptr3 = l2 ?? IntPtr.Zero,
                    ptr4 = l3 ?? IntPtr.Zero
                }
            };
            xev.ClientMessageEvent.ptr4 = l4 ?? IntPtr.Zero;
            XSendEvent(_x11.Display, _x11.RootWindow, false,
                new IntPtr((int)(EventMask.SubstructureRedirectMask | EventMask.SubstructureNotifyMask)), ref xev);

        }

        void BeginMoveResize(NetWmMoveResize side)
        {
            var pos = GetCursorPos(_x11);
            XUngrabPointer(_x11.Display, _x11.LastActivityTimestamp);
            SendNetWMMessage (_x11.Atoms._NET_WM_MOVERESIZE, (IntPtr) pos.x, (IntPtr) pos.y,
                (IntPtr) side,
                (IntPtr) 1, (IntPtr)1); // left button
        }

        public void BeginMoveDrag()
        {
            BeginMoveResize(NetWmMoveResize._NET_WM_MOVERESIZE_MOVE);
        }

        public void BeginResizeDrag(WindowEdge edge)
        {
            var side = NetWmMoveResize._NET_WM_MOVERESIZE_CANCEL;
            if (edge == WindowEdge.East)
                side = NetWmMoveResize._NET_WM_MOVERESIZE_SIZE_RIGHT;
            if (edge == WindowEdge.North)
                side = NetWmMoveResize._NET_WM_MOVERESIZE_SIZE_TOP;
            if (edge == WindowEdge.South)
                side = NetWmMoveResize._NET_WM_MOVERESIZE_SIZE_BOTTOM;
            if (edge == WindowEdge.West)
                side = NetWmMoveResize._NET_WM_MOVERESIZE_SIZE_LEFT;
            if (edge == WindowEdge.NorthEast)
                side = NetWmMoveResize._NET_WM_MOVERESIZE_SIZE_TOPRIGHT;
            if (edge == WindowEdge.NorthWest)
                side = NetWmMoveResize._NET_WM_MOVERESIZE_SIZE_TOPLEFT;
            if (edge == WindowEdge.SouthEast)
                side = NetWmMoveResize._NET_WM_MOVERESIZE_SIZE_BOTTOMRIGHT;
            if (edge == WindowEdge.SouthWest)
                side = NetWmMoveResize._NET_WM_MOVERESIZE_SIZE_BOTTOMLEFT;
            BeginMoveResize(side);
        }
        
        public void SetTitle(string title)
        {
            var data = Encoding.UTF8.GetBytes(title);
            fixed (void* pdata = data)
            {
                XChangeProperty(_x11.Display, _handle, _x11.Atoms._NET_WM_NAME, _x11.Atoms.UTF8_STRING, 8,
                    PropertyMode.Replace, pdata, data.Length);
                XStoreName(_x11.Display, _handle, title);
            }
        }

        public void SetMinMaxSize(Size minSize, Size maxSize)
        {
            _minMaxSize = (minSize, maxSize);
            UpdateSizeHits();
        }

        public void SetTopmost(bool value)
        {
            SendNetWMMessage(_x11.Atoms._NET_WM_STATE,
                (IntPtr)(value ? 1 : 0), _x11.Atoms._NET_WM_STATE_ABOVE, IntPtr.Zero);
        }

        public void ShowDialog(IWindowImpl parent)
        {
            SetTransientParent((X11Window)parent);
            ShowCore();
        }

        public void SetIcon(IWindowIconImpl icon)
        {
            //TODO
        }

        public void ShowTaskbarIcon(bool value)
        {
            SendNetWMMessage(_x11.Atoms._NET_WM_STATE,
                (IntPtr)(value ? 0 : 1), _x11.Atoms._NET_WM_STATE_SKIP_TASKBAR, IntPtr.Zero);
        }        
    }
}
