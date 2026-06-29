using System;

namespace AppCore.Runtime.Core.Models
{
    /// <summary>
    /// Стек с ограниченной ёмкостью на основе кольцевого буфера.
    /// При переполнении автоматически вытесняет самый старый элемент (дно стека).
    /// Все операции выполняются за O(1) и не вызывают аллокаций после создания.
    /// </summary>
    /// <typeparam name="T">Тип хранимых элементов.</typeparam>
    internal class BoundedStack<T>
    {
        private readonly T[] _buffer;
        private int _bottom;
        private int _count;

        /// <summary>Текущее количество элементов в стеке.</summary>
        public int Count => _count;

        /// <summary>Максимальная ёмкость стека, заданная при создании.</summary>
        public int Capacity => _buffer.Length;

        /// <summary>Возвращает <c>true</c>, если стек не содержит элементов.</summary>
        public bool IsEmpty => _count == 0;

        /// <summary>
        /// Возвращает <c>true</c>, если стек заполнен.
        /// Следующий <see cref="Push"/> приведёт к вытеснению самого старого элемента.
        /// </summary>
        public bool IsFull => _count == _buffer.Length;

        /// <summary>
        /// Создаёт новый стек с заданной максимальной ёмкостью.
        /// </summary>
        /// <param name="capacity">Максимальное число элементов. Должно быть больше нуля.</param>
        /// <exception cref="ArgumentOutOfRangeException">Если <paramref name="capacity"/> меньше или равно нулю.</exception>
        public BoundedStack(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _buffer = new T[capacity];
        }

        /// <summary>
        /// Верх стека — самый свежий добавленный элемент.
        /// Эквивалент <see cref="Peek"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Если стек пуст.</exception>
        public T First
        {
            get
            {
                if (_count == 0) throw new InvalidOperationException("Stack is empty");
                return _buffer[(_bottom + _count - 1) % _buffer.Length];
            }
        }

        /// <summary>
        /// Дно стека — самый старый элемент.
        /// Именно он будет вытеснен при следующем <see cref="Push"/>, если стек уже заполнен.
        /// </summary>
        /// <exception cref="InvalidOperationException">Если стек пуст.</exception>
        public T Last
        {
            get
            {
                if (_count == 0) throw new InvalidOperationException("Stack is empty");
                return _buffer[_bottom];
            }
        }

        /// <summary>
        /// Добавляет элемент на верх стека.
        /// Если стек заполнен, самый старый элемент (дно) автоматически удаляется.
        /// </summary>
        /// <param name="item">Добавляемый элемент.</param>
        public void Push(T item)
        {
            int topNext = (_bottom + _count) % _buffer.Length;
            _buffer[topNext] = item;

            if (_count == _buffer.Length) _bottom = (_bottom + 1) % _buffer.Length;
            else _count++;
        }

        /// <summary>
        /// Удаляет и возвращает элемент с верха стека.
        /// </summary>
        /// <returns>Самый свежий добавленный элемент.</returns>
        /// <exception cref="InvalidOperationException">Если стек пуст.</exception>
        public T Pop()
        {
            if (_count == 0) throw new InvalidOperationException("Stack is empty");
            int topIndex = (_bottom + _count - 1) % _buffer.Length;
            T item = _buffer[topIndex];
            _buffer[topIndex] = default; // помогаем GC, если T — ссылочный
            _count--;
            return item;
        }

        /// <summary>
        /// Возвращает элемент с верха стека без его удаления.
        /// </summary>
        /// <returns>Самый свежий добавленный элемент.</returns>
        /// <exception cref="InvalidOperationException">Если стек пуст.</exception>
        public T Peek()
        {
            if (_count == 0) throw new InvalidOperationException("Stack is empty");
            return _buffer[(_bottom + _count - 1) % _buffer.Length];
        }

        /// <summary>
        /// Доступ к элементам по индексу относительно верха стека.
        /// Индекс <c>0</c> соответствует верху (<see cref="First"/>),
        /// индекс <c>Count - 1</c> — дну (<see cref="Last"/>).
        /// Удобно для отрисовки истории действий.
        /// </summary>
        /// <param name="indexFromTop">Индекс, отсчитываемый от верха стека.</param>
        /// <returns>Элемент по указанному индексу.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Если индекс выходит за пределы диапазона <c>[0; Count)</c>.
        /// </exception>
        public T this[int indexFromTop]
        {
            get
            {
                if ((uint)indexFromTop >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(indexFromTop));
                int i = (_bottom + _count - 1 - indexFromTop) % _buffer.Length;
                if (i < 0) i += _buffer.Length;
                return _buffer[i];
            }
        }

        /// <summary>
        /// Полностью очищает стек и обнуляет ссылки в буфере,
        /// чтобы не удерживать объекты от сборки мусора.
        /// </summary>
        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _bottom = 0;
            _count = 0;
        }
    }
}