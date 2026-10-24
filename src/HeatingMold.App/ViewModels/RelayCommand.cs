using System;
using System.Windows.Input;

namespace HeatingMold.App.ViewModels;

/// <summary>WPF 버튼에서 매개변수 없는 화면 명령을 호출합니다.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    /// <summary>명령을 실행할 화면 동작을 보관합니다.</summary>
    public RelayCommand(Action execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>화면 입력 검증은 실행 경로에서 수행하므로 명령은 항상 활성화합니다.</summary>
    public bool CanExecute(object parameter) => true;

    /// <summary>연결된 화면 동작을 호출합니다.</summary>
    public void Execute(object parameter) => _execute();

    /// <summary>WPF 명령 재평가 구독을 허용합니다.</summary>
    public event EventHandler CanExecuteChanged
    {
        add { }
        remove { }
    }
}
